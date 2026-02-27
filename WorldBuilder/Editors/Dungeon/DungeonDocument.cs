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
}
