using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using WorldBuilder.Shared.Lib;
using WorldBuilder.ViewModels;

namespace WorldBuilder.Editors.Dungeon {

    public class RoomEntry {
        public uint EnvironmentFileId { get; set; }
        public ushort EnvironmentId { get; set; }
        public ushort CellStructureIndex { get; set; }
        public int PortalCount { get; set; }
        public int VertexCount { get; set; }
        public int PolygonCount { get; set; }
        public List<ushort> PortalPolygonIds { get; set; } = new();
        public List<ushort> DefaultSurfaces { get; set; } = new();

        public string DisplayName => $"Env {EnvironmentFileId:X8} / Cell {CellStructureIndex}";
        public string DetailText => $"{PortalCount} portals, {PolygonCount} polys, {VertexCount} verts";
    }

    public partial class RoomPaletteViewModel : ViewModelBase {
        private readonly IDatReaderWriter _dats;
        private List<RoomEntry> _allRooms = new();

        [ObservableProperty] private ObservableCollection<RoomEntry> _filteredRooms = new();
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
        }

        partial void OnSelectedRoomChanged(RoomEntry? value) {
            if (value != null) {
                RoomSelected?.Invoke(this, value);
            }
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
