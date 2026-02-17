using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Numerics;
using WorldBuilder.Shared.Documents;
using WorldBuilder.Shared.Models;

namespace WorldBuilder.Services {
    public class StampLibraryManager {
        private const string StampDirectory = "Stamps";
        private readonly ObservableCollection<TerrainStamp> _stamps = new();

        public ObservableCollection<TerrainStamp> Stamps => _stamps;

        public StampLibraryManager() {
            LoadStampsFromDisk();
        }

        public void SaveStamp(TerrainStamp stamp, string filename) {
            Directory.CreateDirectory(StampDirectory);
            var path = Path.Combine(StampDirectory, $"{filename}.stamp");

            using var writer = new BinaryWriter(File.OpenWrite(path));

            // Add to memory
            _stamps.Add(stamp);

            // Header
            writer.Write("ACSTAMP"); // Magic bytes
            writer.Write((byte)1);   // Version

            // Metadata
            writer.Write(stamp.Name);
            writer.Write(stamp.Description);
            writer.Write(stamp.Created.ToBinary());
            writer.Write((ushort)stamp.WidthInVertices);
            writer.Write((ushort)stamp.HeightInVertices);
            writer.Write(stamp.OriginalWorldPosition.X);
            writer.Write(stamp.OriginalWorldPosition.Y);
            writer.Write(stamp.SourceLandblockId);

            // Height data
            writer.Write(stamp.Heights.Length);
            writer.Write(stamp.Heights);

            // Terrain type data
            writer.Write(stamp.TerrainTypes.Length);
            foreach (var terrain in stamp.TerrainTypes) {
                writer.Write(terrain);
            }

            // Objects (optional)
            writer.Write(stamp.Objects.Count);
            foreach (var obj in stamp.Objects) {
                WriteStaticObject(writer, obj);
            }
        }

        public TerrainStamp? LoadStamp(string filename) {
            var path = Path.Combine(StampDirectory, $"{filename}.stamp");
            if (!File.Exists(path)) return null;

            using var reader = new BinaryReader(File.OpenRead(path));

            // Validate header
            var magic = reader.ReadString();
            if (magic != "ACSTAMP") return null;

            var version = reader.ReadByte();
            if (version != 1) return null;

            // Read metadata
            var stamp = new TerrainStamp {
                Name = reader.ReadString(),
                Description = reader.ReadString(),
                Created = DateTime.FromBinary(reader.ReadInt64()),
                WidthInVertices = reader.ReadUInt16(),
                HeightInVertices = reader.ReadUInt16(),
                OriginalWorldPosition = new Vector2(
                    reader.ReadSingle(),
                    reader.ReadSingle()),
                SourceLandblockId = reader.ReadUInt16()
            };

            // Read height data
            int heightCount = reader.ReadInt32();
            stamp.Heights = reader.ReadBytes(heightCount);

            // Read terrain types
            int terrainCount = reader.ReadInt32();
            stamp.TerrainTypes = new ushort[terrainCount];
            for (int i = 0; i < terrainCount; i++) {
                stamp.TerrainTypes[i] = reader.ReadUInt16();
            }

            // Read objects
            int objectCount = reader.ReadInt32();
            for (int i = 0; i < objectCount; i++) {
                stamp.Objects.Add(ReadStaticObject(reader));
            }

            return stamp.IsValid() ? stamp : null;
        }

        private void LoadStampsFromDisk() {
            if (!Directory.Exists(StampDirectory)) return;

            foreach (var file in Directory.GetFiles(StampDirectory, "*.stamp")) {
                var filename = Path.GetFileNameWithoutExtension(file);
                var stamp = LoadStamp(filename);
                if (stamp != null) {
                    _stamps.Add(stamp);
                }
            }
        }

        private void WriteStaticObject(BinaryWriter writer, StaticObject obj) {
            writer.Write(obj.Id);
            writer.Write(obj.IsSetup);
            writer.Write(obj.Origin.X);
            writer.Write(obj.Origin.Y);
            writer.Write(obj.Origin.Z);
            writer.Write(obj.Orientation.X);
            writer.Write(obj.Orientation.Y);
            writer.Write(obj.Orientation.Z);
            writer.Write(obj.Orientation.W);
            writer.Write(obj.Scale.X);
            writer.Write(obj.Scale.Y);
            writer.Write(obj.Scale.Z);
        }

        private StaticObject ReadStaticObject(BinaryReader reader) {
            return new StaticObject {
                Id = reader.ReadUInt32(),
                IsSetup = reader.ReadBoolean(),
                Origin = new Vector3(
                    reader.ReadSingle(),
                    reader.ReadSingle(),
                    reader.ReadSingle()),
                Orientation = new Quaternion(
                    reader.ReadSingle(),
                    reader.ReadSingle(),
                    reader.ReadSingle(),
                    reader.ReadSingle()),
                Scale = new Vector3(
                    reader.ReadSingle(),
                    reader.ReadSingle(),
                    reader.ReadSingle())
            };
        }
    }
}
