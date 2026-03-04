using Avalonia.Input;
using System;
using System.Collections.ObjectModel;
using System.Numerics;
using WorldBuilder.Lib;

namespace WorldBuilder.Editors.Dungeon.Tools {

    public class PortalConnectTool : DungeonToolBase {
        public override string Name => "Connect Doorways";
        public override string IconGlyph => "\u2194"; // ↔

        private static readonly ObservableCollection<DungeonSubToolBase> _empty = new();
        public override ObservableCollection<DungeonSubToolBase> AllSubTools => _empty;

        private ushort _sourceCellNum;
        private ushort _sourcePolyId;
        private bool _hasSource;

        public override void OnActivated() {
            _hasSource = false;
            StatusText = "Click an open doorway to start connecting";
        }

        public override void OnDeactivated() {
            _hasSource = false;
            StatusText = "";
        }

        public override bool HandleMouseDown(MouseState mouseState, DungeonEditingContext ctx) {
            if (!mouseState.LeftPressed || mouseState.RightPressed) return false;
            if (ctx.Scene == null || ctx.Document == null) return false;

            var portal = FindNearestOpenPortal(mouseState, ctx);
            if (portal == null) {
                StatusText = _hasSource
                    ? "No open doorway under cursor — click an open doorway, or Escape to cancel"
                    : "Click an open doorway to start connecting";
                return false;
            }

            var (cellNum, polyId) = portal.Value;

            if (!_hasSource) {
                _sourceCellNum = cellNum;
                _sourcePolyId = polyId;
                _hasSource = true;
                StatusText = $"Source: cell 0x{cellNum:X4} portal 0x{polyId:X4} — now click the target doorway";
                return true;
            }

            // Second click — validate and connect
            if (cellNum == _sourceCellNum && polyId == _sourcePolyId) {
                StatusText = "Can't connect a doorway to itself — click a different doorway";
                return true;
            }

            // Optional compatibility check
            if (ctx.GeometryCache != null) {
                var srcCell = ctx.Document.GetCell(_sourceCellNum);
                var tgtCell = ctx.Document.GetCell(cellNum);
                if (srcCell != null && tgtCell != null &&
                    !ctx.GeometryCache.AreCompatible(
                        srcCell.EnvironmentId, srcCell.CellStructure, _sourcePolyId,
                        tgtCell.EnvironmentId, tgtCell.CellStructure, polyId)) {
                    StatusText = "Portals are not geometrically compatible — try a different pair";
                    return true;
                }
            }

            var cmd = new ConnectPortalCommand(_sourceCellNum, _sourcePolyId, cellNum, polyId);
            ctx.CommandHistory.Execute(cmd, ctx.Document);
            ctx.RefreshRendering();

            StatusText = $"Connected 0x{_sourceCellNum:X4}:0x{_sourcePolyId:X4} \u2194 0x{cellNum:X4}:0x{polyId:X4} — click another doorway or Escape";
            _hasSource = false;
            return true;
        }

        public override bool HandleMouseUp(MouseState mouseState, DungeonEditingContext ctx) => false;

        public override bool HandleMouseMove(MouseState mouseState, DungeonEditingContext ctx) {
            if (ctx.Scene == null) return false;

            var portal = FindNearestOpenPortal(mouseState, ctx);
            if (portal != null) {
                var (cellNum, polyId) = portal.Value;
                if (_hasSource)
                    ctx.SetStatus($"Target: cell 0x{cellNum:X4} portal 0x{polyId:X4}");
                else
                    ctx.SetStatus($"Source: cell 0x{cellNum:X4} portal 0x{polyId:X4}");
            }
            return false;
        }

        public override bool HandleKeyDown(KeyEventArgs e, DungeonEditingContext ctx) {
            if (e.Key == Key.Escape) {
                if (_hasSource) {
                    _hasSource = false;
                    StatusText = "Cancelled — click an open doorway to start connecting";
                    return true;
                }
            }
            return false;
        }

        private static (ushort cellNum, ushort polyId)? FindNearestOpenPortal(
            MouseState mouseState, DungeonEditingContext ctx) {

            if (ctx.Scene == null) return null;
            var indicators = ctx.Scene.OpenPortalIndicators;
            if (indicators.Count == 0) return null;

            var ray = ctx.ComputeRay(mouseState);
            if (ray == null) return null;
            var (origin, dir) = ray.Value;

            float bestDist = float.MaxValue;
            int bestIdx = -1;
            const float maxPickRadius = 5f;

            for (int i = 0; i < indicators.Count; i++) {
                var ind = indicators[i];
                var toPoint = ind.Centroid - origin;
                float proj = Vector3.Dot(toPoint, dir);
                if (proj < 0) continue;
                var closest = origin + dir * proj;
                float dist = (ind.Centroid - closest).Length();
                if (dist < bestDist && dist < maxPickRadius) {
                    bestDist = dist;
                    bestIdx = i;
                }
            }

            if (bestIdx < 0) return null;
            return (indicators[bestIdx].CellNum, indicators[bestIdx].PolyId);
        }
    }
}
