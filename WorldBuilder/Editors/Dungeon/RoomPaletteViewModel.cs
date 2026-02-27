using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using WorldBuilder.Shared.Lib;
using WorldBuilder.ViewModels;

namespace WorldBuilder.Editors.Dungeon {

    public partial class RoomEntry : ObservableObject {
        public uint EnvironmentFileId { get; set; }
        public ushort EnvironmentId { get; set; }
        public ushort CellStructureIndex { get; set; }
        public int PortalCount { get; set; }
        public int VertexCount { get; set; }
        public int PolygonCount { get; set; }
        public List<ushort> PortalPolygonIds { get; set; } = new();
        public List<ushort> DefaultSurfaces { get; set; } = new();

        [ObservableProperty]
        private WriteableBitmap? _thumbnail;

        /// <summary>When set by preset loader, shows a friendly name instead of the raw ID.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DisplayName))]
        private string? _presetDisplayName;

        public string DisplayName => !string.IsNullOrEmpty(PresetDisplayName) ? PresetDisplayName : $"0x{EnvironmentFileId:X8} / #{CellStructureIndex}";
        public string DetailText => $"{PortalCount} portal{(PortalCount != 1 ? "s" : "")}, {PolygonCount} poly, {VertexCount} vert";
    }

    public partial class RoomPaletteViewModel : ViewModelBase {
        private readonly IDatReaderWriter _dats;
        private List<RoomEntry> _allRooms = new();

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(RoomsToShow))]
        private ObservableCollection<RoomEntry> _filteredRooms = new();
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(RoomsToShow))]
        private ObservableCollection<RoomEntry> _starterRooms = new();
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(RoomsToShow))]
        private bool _showStarterMode = true;

        /// <summary>Rooms to display: Starter presets or filtered browse list.</summary>
        public IEnumerable<RoomEntry> RoomsToShow => ShowStarterMode && StarterRooms.Count > 0 ? StarterRooms : FilteredRooms;
        [ObservableProperty] private RoomEntry? _selectedRoom;
        [ObservableProperty] private string _searchText = "";
        [ObservableProperty] private string _statusText = "Loading...";
        [ObservableProperty] private bool _isLoaded;
        [ObservableProperty] private int _minPortals;
        [ObservableProperty] private int _maxPortals = 99;

        public event EventHandler<RoomEntry>? RoomSelected;

        public RoomPaletteViewModel(IDatReaderWriter dats) {
            _dats = dats;
        }

        /// <summary>
        /// Load all Environment objects and their CellStructs from portal.dat.
        /// Should be called once on a background thread.
        /// </summary>
        public async Task LoadRoomsAsync() {
            var rooms = await Task.Run(() => {
                var result = new List<RoomEntry>();
                var envIds = _dats.Dats.Portal
                    .GetAllIdsOfType<DatReaderWriter.DBObjs.Environment>()
                    .OrderBy(id => id)
                    .ToArray();

                foreach (var envFileId in envIds) {
                    if (!_dats.TryGet<DatReaderWriter.DBObjs.Environment>(envFileId, out var env))
                        continue;

                    ushort envId = (ushort)(envFileId & 0x0000FFFF);

                    foreach (var kvp in env.Cells) {
                        var cellStruct = kvp.Value;
                        var portalIds = PortalSnapper.GetPortalPolygonIds(cellStruct);

                        result.Add(new RoomEntry {
                            EnvironmentFileId = envFileId,
                            EnvironmentId = envId,
                            CellStructureIndex = (ushort)kvp.Key,
                            PortalCount = portalIds.Count,
                            PortalPolygonIds = portalIds,
                            VertexCount = cellStruct.VertexArray?.Vertices?.Count ?? 0,
                            PolygonCount = cellStruct.Polygons?.Count ?? 0,
                        });
                    }
                }

                return result;
            });

            _allRooms = rooms;
            IsLoaded = true;
            StatusText = $"{_allRooms.Count} rooms loaded";
            ApplyFilter();

            _ = Task.Run(() => PopulateDefaultSurfaces(rooms));
            _ = GenerateThumbnailsAsync();
            LoadStarterPresets(rooms);
        }

        private void LoadStarterPresets(List<RoomEntry> allRooms) {
            try {
                var path = Path.Combine(
                    System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                    "ACME WorldBuilder", "dungeon_room_analysis.json");
                if (!File.Exists(path)) return;

                var json = File.ReadAllText(path);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (!root.TryGetProperty("topStarterCandidates", out var arr)) return;

                var roomLookup = allRooms.ToDictionary(r => (r.EnvironmentFileId, r.CellStructureIndex));
                var starters = new List<RoomEntry>();
                var names = new[] { "Dead End", "Corridor", "T-Junction", "Crossing", "Complex" };

                foreach (var item in arr.EnumerateArray()) {
                    if (!item.TryGetProperty("envFileId", out var e) || !item.TryGetProperty("cellStructIndex", out var c)) continue;
                    var envFileId = e.GetUInt32();
                    var cellStructIndex = (ushort)c.GetUInt16();
                    var portalCount = item.TryGetProperty("portalCount", out var pc) ? pc.GetInt32() : 0;
                    var usageCount = item.TryGetProperty("usageCount", out var uc) ? uc.GetInt32() : 0;

                    if (!roomLookup.TryGetValue((envFileId, cellStructIndex), out var room)) continue;

                    var typeName = portalCount >= 1 && portalCount <= names.Length ? names[portalCount - 1] : (portalCount == 0 ? "Room" : "Complex");
                    var used = usageCount >= 1000 ? $"{usageCount / 1000}k" : usageCount.ToString();
                    var baseName = $"{typeName} ({portalCount}P, used {used})";

                    // Add sample dungeon names if available (e.g. "from A Red Rat Lair")
                    var dungeonNames = new List<string>();
                    if (item.TryGetProperty("sampleDungeonNames", out var dArr)) {
                        foreach (var d in dArr.EnumerateArray())
                            dungeonNames.Add(d.GetString() ?? "");
                    }
                    var fromPart = dungeonNames.Count > 0
                        ? $" — e.g. {string.Join(", ", dungeonNames.Where(x => !string.IsNullOrEmpty(x)).Take(2))}"
                        : "";
                    room.PresetDisplayName = baseName + fromPart;

                    starters.Add(room);
                }

                StarterRooms = new ObservableCollection<RoomEntry>(starters);
                if (starters.Count > 0)
                    StatusText = $"{_allRooms.Count} rooms, {starters.Count} recommended";
            }
            catch (Exception ex) {
                Console.WriteLine($"[RoomPalette] LoadStarterPresets: {ex.Message}");
            }
        }

        /// <summary>
        /// Reload starter presets from dungeon_room_analysis.json (e.g. after Analyze Rooms).
        /// Call this when the analysis file has been updated.
        /// </summary>
        public void ReloadStarterPresets() {
            if (_allRooms.Count == 0) return;
            LoadStarterPresets(_allRooms);
        }

        private async Task GenerateThumbnailsAsync() {
            await Task.Delay(500);
            var snapshot = FilteredRooms.ToArray();
            foreach (var room in snapshot) {
                if (room.Thumbnail != null) continue;
                var thumb = await Task.Run(() => RenderRoomThumbnail(room));
                if (thumb != null) {
                    Dispatcher.UIThread.Post(() => room.Thumbnail = thumb);
                }
            }
        }

        private WriteableBitmap? RenderRoomThumbnail(RoomEntry room) {
            const int size = 96;
            try {
                uint envFileId = room.EnvironmentFileId;
                if (!_dats.TryGet<DatReaderWriter.DBObjs.Environment>(envFileId, out var env)) return null;
                if (!env.Cells.TryGetValue(room.CellStructureIndex, out var cellStruct)) return null;

                if (cellStruct.VertexArray?.Vertices == null || cellStruct.Polygons == null)
                    return null;

                var verts = cellStruct.VertexArray.Vertices;
                if (verts.Count < 3) return null;

                // Isometric projection: 30 deg elevation, 45 deg azimuth
                const float cosA = 0.866f; // cos(30)
                const float sinA = 0.5f;   // sin(30)

                Vector2 ProjectIso(Vector3 p) => new Vector2(
                    (p.X - p.Y) * cosA,
                    (p.X + p.Y) * sinA - p.Z
                );

                // Compute projected bounds for fitting
                float sMinX = float.MaxValue, sMaxX = float.MinValue;
                float sMinY = float.MaxValue, sMaxY = float.MinValue;
                foreach (var v in verts.Values) {
                    var s = ProjectIso(v.Origin);
                    if (s.X < sMinX) sMinX = s.X;
                    if (s.X > sMaxX) sMaxX = s.X;
                    if (s.Y < sMinY) sMinY = s.Y;
                    if (s.Y > sMaxY) sMaxY = s.Y;
                }

                float rangeX = sMaxX - sMinX;
                float rangeY = sMaxY - sMinY;
                if (rangeX < 0.1f && rangeY < 0.1f) return null;
                float range = MathF.Max(rangeX, rangeY) * 1.1f;
                float centerSX = (sMinX + sMaxX) * 0.5f;
                float centerSY = (sMinY + sMaxY) * 0.5f;
                int margin = 3;
                float scale = (size - margin * 2) / range;

                int ToPixX(float sx) => Math.Clamp((int)((sx - centerSX) * scale + size * 0.5f), 0, size - 1);
                int ToPixY(float sy) => Math.Clamp((int)((sy - centerSY) * scale + size * 0.5f), 0, size - 1);

                var pixels = new byte[size * size * 4];
                for (int i = 0; i < pixels.Length; i += 4) {
                    pixels[i] = 14; pixels[i + 1] = 10; pixels[i + 2] = 22; pixels[i + 3] = 255;
                }

                // Build polygon render list with depth sorting
                var polyList = new List<(int key, float depth, bool isPortal, bool isFloor, bool isCeiling,
                    List<(int x, int y)> screenPts, Vector3 normal)>();

                var portalIds = cellStruct.Portals != null ? new HashSet<ushort>(cellStruct.Portals) : new HashSet<ushort>();

                foreach (var kvp in cellStruct.Polygons) {
                    var poly = kvp.Value;
                    if (poly.VertexIds.Count < 3) continue;

                    bool isPortal = portalIds.Contains(kvp.Key);

                    var pts3d = new List<Vector3>();
                    var screenPts = new List<(int x, int y)>();
                    foreach (var vid in poly.VertexIds) {
                        if (!verts.TryGetValue((ushort)vid, out var vtx)) break;
                        pts3d.Add(vtx.Origin);
                        var sp = ProjectIso(vtx.Origin);
                        screenPts.Add((ToPixX(sp.X), ToPixY(sp.Y)));
                    }
                    if (pts3d.Count < 3 || screenPts.Count != poly.VertexIds.Count) continue;

                    // Compute polygon normal from first 3 verts
                    var edge1 = pts3d[1] - pts3d[0];
                    var edge2 = pts3d[2] - pts3d[0];
                    var normal = Vector3.Normalize(Vector3.Cross(edge1, edge2));

                    bool isFloor = !isPortal && normal.Z > 0.7f;
                    bool isCeiling = !isPortal && normal.Z < -0.7f;

                    // Depth = average of projected Y (higher Y = further back in isometric)
                    float centroidX = 0, centroidY = 0, centroidZ = 0;
                    foreach (var p in pts3d) { centroidX += p.X; centroidY += p.Y; centroidZ += p.Z; }
                    centroidX /= pts3d.Count; centroidY /= pts3d.Count; centroidZ /= pts3d.Count;
                    float depth = (centroidX + centroidY) * sinA - centroidZ;

                    polyList.Add((kvp.Key, depth, isPortal, isFloor, isCeiling, screenPts, normal));
                }

                // Sort back-to-front (largest depth first = furthest away drawn first)
                polyList.Sort((a, b) => b.depth.CompareTo(a.depth));

                // Draw polygons
                foreach (var (key, depth, isPortal, isFloor, isCeiling, screenPts, normal) in polyList) {
                    if (isPortal) {
                        // Portal: bright green semi-transparent fill + outline
                        FillPolygon(pixels, size, screenPts, 40, 180, 60);
                        DrawPolyOutline(pixels, size, screenPts, 80, 255, 100);
                    }
                    else if (isFloor) {
                        // Floor: dark tinted fill + subtle outline
                        FillPolygon(pixels, size, screenPts, 28, 22, 40);
                        DrawPolyOutline(pixels, size, screenPts, 60, 50, 80);
                    }
                    else if (isCeiling) {
                        // Ceiling: slightly lighter outline only
                        DrawPolyOutline(pixels, size, screenPts, 50, 45, 70);
                    }
                    else {
                        // Wall: shaded based on facing direction for depth cue
                        float shade = MathF.Abs(normal.X * 0.6f + normal.Y * 0.3f) + 0.4f;
                        shade = MathF.Min(shade, 1f);
                        byte wr = (byte)(90 * shade);
                        byte wg = (byte)(80 * shade);
                        byte wb = (byte)(120 * shade);
                        FillPolygon(pixels, size, screenPts, (byte)(wr / 2), (byte)(wg / 2), (byte)(wb / 2));
                        DrawPolyOutline(pixels, size, screenPts, wr, wg, wb);
                    }
                }

                var bitmap = new WriteableBitmap(new PixelSize(size, size), new Avalonia.Vector(96, 96),
                    Avalonia.Platform.PixelFormat.Rgba8888);
                using (var fb = bitmap.Lock()) {
                    Marshal.Copy(pixels, 0, fb.Address, Math.Min(pixels.Length, fb.RowBytes * size));
                }
                return bitmap;
            }
            catch { return null; }
        }

        private static void DrawPolyOutline(byte[] pixels, int size, List<(int x, int y)> pts, byte r, byte g, byte b) {
            for (int i = 0; i < pts.Count; i++) {
                int next = (i + 1) % pts.Count;
                DrawLine(pixels, size, pts[i].x, pts[i].y, pts[next].x, pts[next].y, r, g, b);
            }
        }

        private static void FillPolygon(byte[] pixels, int size, List<(int x, int y)> pts, byte r, byte g, byte b) {
            if (pts.Count < 3) return;
            // Fan triangulation from first vertex (works for convex polygons)
            for (int i = 1; i < pts.Count - 1; i++) {
                FillTriangle(pixels, size, pts[0].x, pts[0].y, pts[i].x, pts[i].y, pts[i + 1].x, pts[i + 1].y, r, g, b);
            }
        }

        private static void FillTriangle(byte[] pixels, int size,
            int x0, int y0, int x1, int y1, int x2, int y2, byte r, byte g, byte b) {
            // Sort vertices by Y
            if (y0 > y1) { (x0, y0, x1, y1) = (x1, y1, x0, y0); }
            if (y0 > y2) { (x0, y0, x2, y2) = (x2, y2, x0, y0); }
            if (y1 > y2) { (x1, y1, x2, y2) = (x2, y2, x1, y1); }

            int minY = Math.Max(y0, 0);
            int maxY = Math.Min(y2, size - 1);

            for (int y = minY; y <= maxY; y++) {
                float xLeft, xRight;
                if (y < y1) {
                    if (y1 == y0) { xLeft = Math.Min(x0, x1); xRight = Math.Max(x0, x1); }
                    else {
                        float t01 = (float)(y - y0) / (y1 - y0);
                        float t02 = (y2 == y0) ? 0 : (float)(y - y0) / (y2 - y0);
                        xLeft = x0 + t01 * (x1 - x0);
                        xRight = x0 + t02 * (x2 - x0);
                    }
                }
                else {
                    if (y2 == y1) { xLeft = Math.Min(x1, x2); xRight = Math.Max(x1, x2); }
                    else {
                        float t12 = (float)(y - y1) / (y2 - y1);
                        float t02 = (y2 == y0) ? 1 : (float)(y - y0) / (y2 - y0);
                        xLeft = x1 + t12 * (x2 - x1);
                        xRight = x0 + t02 * (x2 - x0);
                    }
                }

                if (xLeft > xRight) (xLeft, xRight) = (xRight, xLeft);
                int startX = Math.Max((int)xLeft, 0);
                int endX = Math.Min((int)xRight, size - 1);

                for (int x = startX; x <= endX; x++) {
                    int idx = (y * size + x) * 4;
                    pixels[idx] = r; pixels[idx + 1] = g; pixels[idx + 2] = b; pixels[idx + 3] = 255;
                }
            }
        }

        private static void DrawLine(byte[] pixels, int size, int x0, int y0, int x1, int y1, byte r, byte g, byte b) {
            int dx = Math.Abs(x1 - x0), dy = Math.Abs(y1 - y0);
            int sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1;
            int err = dx - dy;
            int steps = Math.Max(dx, dy) + 1;
            for (int i = 0; i < steps; i++) {
                if (x0 >= 0 && x0 < size && y0 >= 0 && y0 < size) {
                    int idx = (y0 * size + x0) * 4;
                    pixels[idx] = r; pixels[idx + 1] = g; pixels[idx + 2] = b; pixels[idx + 3] = 255;
                }
                if (x0 == x1 && y0 == y1) break;
                int e2 = 2 * err;
                if (e2 > -dy) { err -= dy; x0 += sx; }
                if (e2 < dx) { err += dx; y0 += sy; }
            }
        }

        /// <summary>
        /// Scans existing dungeon EnvCells to find default surface lists for each Environment+CellStruct.
        /// Rooms without surfaces render as invisible, so this is critical for placing new cells.
        /// </summary>
        private void PopulateDefaultSurfaces(List<RoomEntry> rooms) {
            try {
                var lookup = new Dictionary<(ushort envId, ushort cellStruct), List<ushort>>();
                var lbiIds = _dats.Dats.GetAllIdsOfType<DatReaderWriter.DBObjs.LandBlockInfo>().ToArray();
                if (lbiIds.Length == 0) lbiIds = _dats.Dats.Cell.GetAllIdsOfType<DatReaderWriter.DBObjs.LandBlockInfo>().ToArray();
                Console.WriteLine($"[RoomPalette] Scanning {lbiIds.Length} LandBlockInfo entries for default surfaces");

                foreach (var lbiId in lbiIds) {
                    if (!_dats.TryGet<DatReaderWriter.DBObjs.LandBlockInfo>(lbiId, out var lbi) || lbi.NumCells == 0) continue;
                    uint lbId = lbiId >> 16;

                    for (uint i = 0; i < lbi.NumCells; i++) {
                        uint cellId = (lbId << 16) | (0x0100 + i);
                        if (!_dats.TryGet<DatReaderWriter.DBObjs.EnvCell>(cellId, out var envCell)) continue;
                        if (envCell.Surfaces.Count == 0) continue;

                        var key = (envCell.EnvironmentId, envCell.CellStructure);
                        if (!lookup.ContainsKey(key)) {
                            lookup[key] = new List<ushort>(envCell.Surfaces);
                        }
                    }

                    if (lookup.Count > 5000) break;
                }

                int populated = 0;
                foreach (var room in rooms) {
                    if (room.DefaultSurfaces.Count > 0) continue;
                    var key = (room.EnvironmentId, room.CellStructureIndex);
                    if (lookup.TryGetValue(key, out var surfaces)) {
                        room.DefaultSurfaces = new List<ushort>(surfaces);
                        populated++;
                    }
                }

                Console.WriteLine($"[RoomPalette] Populated default surfaces for {populated}/{rooms.Count} rooms from {lookup.Count} unique EnvCell entries");

                // Diagnostic: dump a sample dungeon's cells and check if they match room palette entries
                uint sampleLbId = 0x01D9;
                uint sampleLbiId = (sampleLbId << 16) | 0xFFFE;
                if (_dats.TryGet<DatReaderWriter.DBObjs.LandBlockInfo>(sampleLbiId, out var sampleLbi) && sampleLbi.NumCells > 0) {
                    Console.WriteLine($"[RoomPalette] === Sample dungeon 0x{sampleLbId:X4}: {sampleLbi.NumCells} cells ===");
                    var roomLookup = new HashSet<(ushort, ushort)>();
                    foreach (var r in rooms) roomLookup.Add((r.EnvironmentId, r.CellStructureIndex));

                    for (uint ci = 0; ci < Math.Min(sampleLbi.NumCells, 5); ci++) {
                        uint cellId = (sampleLbId << 16) | (0x0100 + ci);
                        if (_dats.TryGet<DatReaderWriter.DBObjs.EnvCell>(cellId, out var ec)) {
                            bool inPalette = roomLookup.Contains((ec.EnvironmentId, ec.CellStructure));
                            Console.WriteLine($"  Cell 0x{cellId:X8}: Env=0x{ec.EnvironmentId:X4} CellStruct={ec.CellStructure} " +
                                $"Surfaces={ec.Surfaces.Count} InPalette={inPalette}");
                        }
                    }
                }
            }
            catch (Exception ex) {
                Console.WriteLine($"[RoomPalette] Error populating default surfaces: {ex.Message}");
            }
        }

        partial void OnSearchTextChanged(string value) => ApplyFilter();
        partial void OnMinPortalsChanged(int value) => ApplyFilter();
        partial void OnMaxPortalsChanged(int value) => ApplyFilter();

        private void ApplyFilter() {
            var query = SearchText?.Trim() ?? "";
            var filtered = _allRooms.AsEnumerable();

            filtered = filtered.Where(r => r.PortalCount >= MinPortals && r.PortalCount <= MaxPortals);

            if (!string.IsNullOrEmpty(query)) {
                if (uint.TryParse(query, System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out var hexVal)) {
                    filtered = filtered.Where(r =>
                        r.EnvironmentFileId == hexVal ||
                        r.EnvironmentId == (ushort)hexVal ||
                        r.EnvironmentFileId.ToString("X8").Contains(query, StringComparison.OrdinalIgnoreCase));
                }
                else {
                    filtered = filtered.Where(r =>
                        r.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase));
                }
            }

            FilteredRooms = new ObservableCollection<RoomEntry>(filtered.Take(200));
            _ = GenerateThumbnailsAsync();
        }

        partial void OnSelectedRoomChanged(RoomEntry? value) {
            if (value != null) {
                RoomSelected?.Invoke(this, value);
            }
        }

        /// <summary>
        /// Returns a friendly display name for a room (e.g. "Corridor (2P, used 17k)") given env file id and cell struct.
        /// Returns null if not found in the room list.
        /// </summary>
        public string? GetRoomDisplayName(uint envFileId, ushort cellStructIndex) {
            var room = _allRooms.FirstOrDefault(r => r.EnvironmentFileId == envFileId && r.CellStructureIndex == cellStructIndex);
            return room?.DisplayName;
        }

        /// <summary>
        /// Get the default surfaces for a room from an existing EnvCell that uses the same Environment.
        /// Falls back to empty list if no reference can be found.
        /// </summary>
        public List<ushort> GetDefaultSurfaces(RoomEntry room) {
            if (room.DefaultSurfaces.Count > 0) return room.DefaultSurfaces;

            // Try to find an existing EnvCell using this Environment to get default surfaces
            // For now, return an empty list -- surfaces can be assigned later
            return new List<ushort>();
        }
    }
}
