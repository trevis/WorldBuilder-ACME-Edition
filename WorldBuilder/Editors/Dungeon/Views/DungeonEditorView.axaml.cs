using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
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

        protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e) {
            base.OnAttachedToVisualTree(e);
            var topLevel = TopLevel.GetTopLevel(this);
            topLevel?.AddHandler(KeyDownEvent, OnTopLevelKeyDown, RoutingStrategies.Tunnel);
        }

        protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e) {
            var topLevel = TopLevel.GetTopLevel(this);
            topLevel?.RemoveHandler(KeyDownEvent, OnTopLevelKeyDown);
            base.OnDetachedFromVisualTree(e);
            _viewModel?.Cleanup();
        }

        private void OnTopLevelKeyDown(object? sender, KeyEventArgs e) {
            if (_viewModel == null || !IsEffectivelyVisible) return;
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;

            var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Control;
            if (focused is TextBox) return;

            if (e.Key == Key.C) {
                _viewModel.CopySelectedCells();
                e.Handled = true;
            }
            else if (e.Key == Key.V) {
                _viewModel.PasteCells();
                e.Handled = true;
            }
        }

        private void InitializeComponent() {
            AvaloniaXamlLoader.Load(this);
        }
    }
}
