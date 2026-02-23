using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using WorldBuilder.Lib;
using System;

namespace WorldBuilder.Editors.Dungeon.Views {
    public partial class DungeonEditorView : UserControl {
        private DungeonEditorViewModel? _viewModel;

        public DungeonEditorView() {
            InitializeComponent();

            if (Design.IsDesignMode) return;

            _viewModel = ProjectManager.Instance.GetProjectService<DungeonEditorViewModel>()
                ?? throw new Exception("Failed to get DungeonEditorViewModel");

            DataContext = _viewModel;

            if (ProjectManager.Instance.CurrentProject != null) {
                _viewModel.Init(ProjectManager.Instance.CurrentProject);
            }
        }

        private void InitializeComponent() {
            AvaloniaXamlLoader.Load(this);
        }

        protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e) {
            base.OnDetachedFromVisualTree(e);
            _viewModel?.Cleanup();
        }
    }
}
