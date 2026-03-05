using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using System;
using WorldBuilder.Shared.Documents;

namespace WorldBuilder.Editors.Dungeon.Views {
    public partial class DungeonGraphPanelView : UserControl {
        private DungeonGraphPanelViewModel? _vm;

        public DungeonGraphPanelView() {
            InitializeComponent();
        }

        private void InitializeComponent() {
            AvaloniaXamlLoader.Load(this);
        }

        protected override void OnDataContextChanged(EventArgs e) {
            base.OnDataContextChanged(e);
            if (_vm != null) _vm.RefreshRequested -= OnRefresh;
            _vm = DataContext as DungeonGraphPanelViewModel;
            if (_vm != null) {
                GraphView.DataContext = _vm.Editor;
                _vm.RefreshRequested += OnRefresh;
            }
        }

        private void OnRefresh(DungeonDocument? doc, ushort? selectedCell) {
            GraphView.Refresh(doc, selectedCell);
        }
    }
}
