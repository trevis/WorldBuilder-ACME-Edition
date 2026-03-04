using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;

namespace WorldBuilder.Editors.Dungeon.Views {
    public partial class RoomPaletteView : UserControl {
        public RoomPaletteView() {
            InitializeComponent();
        }

        private void InitializeComponent() {
            AvaloniaXamlLoader.Load(this);
        }

        private void PrefabItem_PointerEntered(object? sender, PointerEventArgs e) {
            if (sender is Control c && c.DataContext is PrefabListEntry entry &&
                DataContext is RoomPaletteViewModel vm) {
                vm.NotifyPrefabHover(entry.Prefab);
            }
        }

        private void PrefabItem_PointerExited(object? sender, PointerEventArgs e) {
            if (DataContext is RoomPaletteViewModel vm) {
                vm.NotifyPrefabHover(null);
            }
        }
    }
}
