using CommunityToolkit.Mvvm.ComponentModel;
using Avalonia.Media.Imaging;

namespace WorldBuilder.Editors.Dungeon {
    public partial class CellSurfaceSlot : ObservableObject {
        public int SlotIndex { get; }
        public ushort SurfaceId { get; }
        public string DisplayText { get; }

        [ObservableProperty]
        private WriteableBitmap? _thumbnail;

        public CellSurfaceSlot(int slotIndex, ushort surfaceId, string displayText) {
            SlotIndex = slotIndex;
            SurfaceId = surfaceId;
            DisplayText = displayText;
        }
    }

    public partial class PrefabListEntry : ObservableObject {
        public DungeonPrefab Prefab { get; }
        public string DisplayName { get; }
        public string DetailText => DetailOverride ?? $"{Prefab.Cells.Count} rooms, {Prefab.OpenPortalCount} open, used {Prefab.UsageCount}x";
        public string? DetailOverride { get; set; }
        public int CellCount => Prefab.Cells.Count;
        public int OpenPortals => Prefab.OpenPortalCount;

        [ObservableProperty]
        private Avalonia.Media.Imaging.WriteableBitmap? _thumbnail;

        public PrefabListEntry(DungeonPrefab prefab, string displayName) {
            Prefab = prefab;
            DisplayName = displayName;
        }
    }

    public class PortalListEntry {
        public int Index { get; }
        public ushort PolygonId { get; }
        public ushort OtherCellId { get; }
        public bool IsConnected { get; }
        public string DisplayText { get; }

        public PortalListEntry(int index, ushort polygonId, ushort otherCellId, bool isConnected, string? connectedRoomName = null) {
            Index = index;
            PolygonId = polygonId;
            OtherCellId = otherCellId;
            IsConnected = isConnected;
            if (isConnected) {
                var target = !string.IsNullOrEmpty(connectedRoomName) ? connectedRoomName : $"Room 0x{otherCellId:X4}";
                DisplayText = $"Door {index + 1} → {target}";
            }
            else {
                DisplayText = $"Door (open)";
            }
        }
    }
}
