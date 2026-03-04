using System.Collections.Generic;
using System.Linq;

namespace WorldBuilder.Editors.Dungeon {

    public class DungeonPrefab {
        public string Signature { get; set; } = "";
        public int SourceLandblock { get; set; }
        public string SourceDungeonName { get; set; } = "";
        public int UsageCount { get; set; } = 1;
        public List<PrefabCell> Cells { get; set; } = new();
        public List<PrefabPortal> InternalPortals { get; set; } = new();
        public List<PrefabOpenFace> OpenFaces { get; set; } = new();
        public int TotalPortals => Cells.Sum(c => c.PortalCount);
        public int OpenPortalCount => OpenFaces.Count;

        public string DisplayName { get; set; } = "";
        public string Category { get; set; } = "";
        public string Style { get; set; } = "";
        public List<string> Tags { get; set; } = new();
    }

    public class PrefabCell {
        public int LocalIndex { get; set; }
        public ushort EnvId { get; set; }
        public ushort CellStruct { get; set; }
        public int PortalCount { get; set; }
        public float OffsetX { get; set; }
        public float OffsetY { get; set; }
        public float OffsetZ { get; set; }
        public float RotX { get; set; }
        public float RotY { get; set; }
        public float RotZ { get; set; }
        public float RotW { get; set; } = 1f;
        public List<ushort> Surfaces { get; set; } = new();
    }

    public class PrefabPortal {
        public int CellIndexA { get; set; }
        public ushort PolyIdA { get; set; }
        public int CellIndexB { get; set; }
        public ushort PolyIdB { get; set; }
    }

    public class PrefabOpenFace {
        public int CellIndex { get; set; }
        public ushort PolyId { get; set; }
        public ushort EnvId { get; set; }
        public ushort CellStruct { get; set; }
        public float NormalX { get; set; }
        public float NormalY { get; set; }
        public float NormalZ { get; set; }
    }

    public class CatalogRoom {
        public ushort EnvId { get; set; }
        public ushort CellStruct { get; set; }
        public string Category { get; set; } = "";
        public string Size { get; set; } = "";
        public string Style { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public int PortalCount { get; set; }
        public int UsageCount { get; set; }
        public float BoundsWidth { get; set; }
        public float BoundsDepth { get; set; }
        public float BoundsHeight { get; set; }
        public List<string> SourceDungeons { get; set; } = new();
        public List<ushort> SampleSurfaces { get; set; } = new();
    }
}
