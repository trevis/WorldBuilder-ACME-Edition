using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DatReaderWriter.DBObjs;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using WorldBuilder.Shared.Documents;
using WorldBuilder.Shared.Lib;
using WorldBuilder.ViewModels;

namespace WorldBuilder.Editors.Landscape.ViewModels {
    /// <summary>
    /// Panel for browsing DAT objects and placing them in the scene.
    /// </summary>
    public partial class ObjectBrowserViewModel : ViewModelBase {
        private readonly TerrainEditingContext _context;
        private readonly IDatReaderWriter _dats;
        private uint[] _allSetupIds = Array.Empty<uint>();
        private uint[] _allGfxObjIds = Array.Empty<uint>();
        private HashSet<uint> _buildingIds = new();
        private bool _buildingIdsLoaded;
        private HashSet<uint> _sceneryIds = new();
        private bool _sceneryIdsLoaded;

        [ObservableProperty] private uint[] _filteredSetupIds = Array.Empty<uint>();
        [ObservableProperty] private uint[] _filteredGfxObjIds = Array.Empty<uint>();
        [ObservableProperty] private string _searchText = "";
        [ObservableProperty] private string _status = "Search for an object by hex ID";

        [ObservableProperty] private bool _showSetups = true;
        [ObservableProperty] private bool _showGfxObjs = true;
        [ObservableProperty] private bool _showBuildingsOnly;
        [ObservableProperty] private bool _showSceneryOnly;

        public ObjectBrowserViewModel(TerrainEditingContext context, IDatReaderWriter dats) {
            _context = context;
            _dats = dats;

            try {
                _allSetupIds = _dats.Dats.Portal.GetAllIdsOfType<Setup>().OrderBy(id => id).ToArray();
                _allGfxObjIds = _dats.Dats.Portal.GetAllIdsOfType<GfxObj>().OrderBy(id => id).ToArray();
                Console.WriteLine($"[ObjectBrowser] Loaded {_allSetupIds.Length} Setups, {_allGfxObjIds.Length} GfxObjs");
            }
            catch (Exception ex) {
                Console.WriteLine($"[ObjectBrowser] Error loading object IDs: {ex.Message}");
            }

            ApplyFilter();

            // Scan building and scenery IDs in background to avoid blocking startup
            Task.Run(LoadBuildingIds);
            Task.Run(LoadSceneryIds);
        }

        private void LoadBuildingIds() {
            try {
                var buildingIds = new HashSet<uint>();

                // Try DatCollection-level enumeration first
                var allLbiIds = _dats.Dats.GetAllIdsOfType<LandBlockInfo>().ToArray();
                Console.WriteLine($"[ObjectBrowser] Found {allLbiIds.Length} LandBlockInfo entries in DAT");

                if (allLbiIds.Length == 0) {
                    // Fallback: try Cell database directly
                    allLbiIds = _dats.Dats.Cell.GetAllIdsOfType<LandBlockInfo>().ToArray();
                    Console.WriteLine($"[ObjectBrowser] Cell fallback: {allLbiIds.Length} LandBlockInfo entries");
                }

                if (allLbiIds.Length == 0) {
                    // Last resort: brute-force scan all possible landblock IDs
                    Console.WriteLine("[ObjectBrowser] Brute-force scanning landblocks for buildings...");
                    for (uint x = 0; x < 255; x++) {
                        for (uint y = 0; y < 255; y++) {
                            var infoId = (uint)(((x << 8) | y) << 16 | 0xFFFE);
                            if (_dats.TryGet<LandBlockInfo>(infoId, out var lbi)) {
                                foreach (var building in lbi.Buildings) {
                                    buildingIds.Add(building.ModelId);
                                }
                            }
                        }
                    }
                }
                else {
                    foreach (var infoId in allLbiIds) {
                        if (_dats.TryGet<LandBlockInfo>(infoId, out var lbi)) {
                            foreach (var building in lbi.Buildings) {
                                buildingIds.Add(building.ModelId);
                            }
                        }
                    }
                }

                _buildingIds = buildingIds;
                _buildingIdsLoaded = true;
                Console.WriteLine($"[ObjectBrowser] Found {_buildingIds.Count} unique building model IDs");
            }
            catch (Exception ex) {
                Console.WriteLine($"[ObjectBrowser] Error scanning building IDs: {ex}");
                _buildingIdsLoaded = true; // Mark loaded so UI doesn't stay stuck on "Loading..."
            }
        }

        private void LoadSceneryIds() {
            try {
                var sceneryIds = new HashSet<uint>();
                var region = _context.TerrainSystem.Region;

                // Collect all unique Scene IDs from the Region's terrain/scene type mappings
                var sceneIds = new HashSet<uint>();
                foreach (var terrainType in region.TerrainInfo.TerrainTypes) {
                    foreach (var sceneTypeIdx in terrainType.SceneTypes) {
                        if (sceneTypeIdx < region.SceneInfo.SceneTypes.Count) {
                            var sceneType = region.SceneInfo.SceneTypes[(int)sceneTypeIdx];
                            foreach (var sceneId in sceneType.Scenes) {
                                sceneIds.Add(sceneId);
                            }
                        }
                    }
                }

                // Load each Scene and collect its object model IDs
                foreach (var sceneId in sceneIds) {
                    if (_dats.TryGet<Scene>(sceneId, out var scene)) {
                        foreach (var obj in scene.Objects) {
                            if (obj.ObjectId != 0) {
                                sceneryIds.Add(obj.ObjectId);
                            }
                        }
                    }
                }

                _sceneryIds = sceneryIds;
                _sceneryIdsLoaded = true;
                Console.WriteLine($"[ObjectBrowser] Found {_sceneryIds.Count} unique scenery model IDs from {sceneIds.Count} scenes");
            }
            catch (Exception ex) {
                Console.WriteLine($"[ObjectBrowser] Error scanning scenery IDs: {ex}");
                _sceneryIdsLoaded = true;
            }
        }

        partial void OnSearchTextChanged(string value) {
            ApplyFilter();
        }

        partial void OnShowSetupsChanged(bool value) {
            ApplyFilter();
        }

        partial void OnShowGfxObjsChanged(bool value) {
            ApplyFilter();
        }

        partial void OnShowBuildingsOnlyChanged(bool value) {
            if (value) ShowSceneryOnly = false;
            ApplyFilter();
        }

        partial void OnShowSceneryOnlyChanged(bool value) {
            if (value) ShowBuildingsOnly = false;
            ApplyFilter();
        }

        private void ApplyFilter() {
            // When buildings filter is active, show building IDs directly
            if (ShowBuildingsOnly) {
                if (!_buildingIdsLoaded) {
                    Status = "Loading building list...";
                    FilteredSetupIds = Array.Empty<uint>();
                    FilteredGfxObjIds = Array.Empty<uint>();
                    return;
                }

                // Split building IDs into Setup vs GfxObj by their ID range
                // Show all buildings regardless of Setups/GfxObjs checkboxes
                IEnumerable<uint> buildingSetups = _buildingIds
                    .Where(id => (id & 0xFF000000) == 0x02000000).OrderBy(id => id);
                IEnumerable<uint> buildingGfxObjs = _buildingIds
                    .Where(id => (id & 0xFF000000) != 0x02000000).OrderBy(id => id);

                // Apply hex search on top
                if (!string.IsNullOrWhiteSpace(SearchText)) {
                    var searchUpper = SearchText.TrimStart('0', 'x', 'X').ToUpperInvariant();
                    if (uint.TryParse(searchUpper, System.Globalization.NumberStyles.HexNumber, null, out _)) {
                        buildingSetups = buildingSetups.Where(id => id.ToString("X8").Contains(searchUpper));
                        buildingGfxObjs = buildingGfxObjs.Where(id => id.ToString("X8").Contains(searchUpper));
                    }
                    else {
                        FilteredSetupIds = Array.Empty<uint>();
                        FilteredGfxObjIds = Array.Empty<uint>();
                        Status = "Invalid hex search";
                        return;
                    }
                }

                var sr = buildingSetups.Take(100).ToArray();
                var gr = buildingGfxObjs.Take(100).ToArray();
                FilteredSetupIds = sr;
                FilteredGfxObjIds = gr;
                Status = $"{_buildingIds.Count} buildings total — showing {sr.Length + gr.Length}";
                return;
            }

            // Scenery filter mode
            if (ShowSceneryOnly) {
                if (!_sceneryIdsLoaded) {
                    Status = "Loading scenery list...";
                    FilteredSetupIds = Array.Empty<uint>();
                    FilteredGfxObjIds = Array.Empty<uint>();
                    return;
                }

                IEnumerable<uint> scenerySetups = _sceneryIds
                    .Where(id => (id & 0xFF000000) == 0x02000000).OrderBy(id => id);
                IEnumerable<uint> sceneryGfxObjs = _sceneryIds
                    .Where(id => (id & 0xFF000000) != 0x02000000).OrderBy(id => id);

                if (!string.IsNullOrWhiteSpace(SearchText)) {
                    var searchUpper = SearchText.TrimStart('0', 'x', 'X').ToUpperInvariant();
                    if (uint.TryParse(searchUpper, System.Globalization.NumberStyles.HexNumber, null, out _)) {
                        scenerySetups = scenerySetups.Where(id => id.ToString("X8").Contains(searchUpper));
                        sceneryGfxObjs = sceneryGfxObjs.Where(id => id.ToString("X8").Contains(searchUpper));
                    }
                    else {
                        FilteredSetupIds = Array.Empty<uint>();
                        FilteredGfxObjIds = Array.Empty<uint>();
                        Status = "Invalid hex search";
                        return;
                    }
                }

                var scsr = scenerySetups.Take(100).ToArray();
                var scgr = sceneryGfxObjs.Take(100).ToArray();
                FilteredSetupIds = scsr;
                FilteredGfxObjIds = scgr;
                Status = $"{_sceneryIds.Count} scenery total — showing {scsr.Length + scgr.Length}";
                return;
            }

            // Normal mode: show all Setups / GfxObjs
            IEnumerable<uint> setups = ShowSetups ? _allSetupIds : Array.Empty<uint>();
            IEnumerable<uint> gfxObjs = ShowGfxObjs ? _allGfxObjIds : Array.Empty<uint>();

            if (!string.IsNullOrWhiteSpace(SearchText)) {
                var searchUpper = SearchText.TrimStart('0', 'x', 'X').ToUpperInvariant();
                if (uint.TryParse(searchUpper, System.Globalization.NumberStyles.HexNumber, null, out _)) {
                    setups = setups.Where(id => id.ToString("X8").Contains(searchUpper));
                    gfxObjs = gfxObjs.Where(id => id.ToString("X8").Contains(searchUpper));
                }
                else {
                    FilteredSetupIds = Array.Empty<uint>();
                    FilteredGfxObjIds = Array.Empty<uint>();
                    Status = "Invalid hex search";
                    return;
                }
            }

            var setupResult = setups.Take(100).ToArray();
            var gfxResult = gfxObjs.Take(100).ToArray();
            FilteredSetupIds = setupResult;
            FilteredGfxObjIds = gfxResult;
            Status = $"Showing {setupResult.Length} Setups, {gfxResult.Length} GfxObjs";
        }

        /// <summary>
        /// Raised when an object is selected for placement, so the editor can switch to the Selector tool.
        /// </summary>
        public event EventHandler? PlacementRequested;

        [RelayCommand]
        private void SelectForPlacement(uint objectId) {
            bool isSetup = (objectId & 0x02000000) != 0;

            _context.ObjectSelection.IsPlacementMode = true;
            _context.ObjectSelection.PlacementPreview = new StaticObject {
                Id = objectId,
                IsSetup = isSetup,
                Origin = Vector3.Zero,
                Orientation = Quaternion.Identity,
                Scale = Vector3.One
            };

            Status = $"Placing 0x{objectId:X8} - click terrain to place, Escape to cancel";
            Console.WriteLine($"[ObjectBrowser] Selected 0x{objectId:X8} for placement (IsSetup={isSetup})");

            PlacementRequested?.Invoke(this, EventArgs.Empty);
        }
    }
}
