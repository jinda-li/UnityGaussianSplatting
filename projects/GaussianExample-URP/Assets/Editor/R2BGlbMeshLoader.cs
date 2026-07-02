// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;

namespace R2B.Editor.GaussianCollision
{
    public static class GlbMeshLoader
    {
        struct Accessor
        {
            public int bufferView;
            public int byteOffset;
            public int count;
            public int componentType;
        }

        public static Mesh LoadFirstMesh(string glbPath)
        {
            if (string.IsNullOrWhiteSpace(glbPath) || !File.Exists(glbPath))
                throw new FileNotFoundException("GLB file not found.", glbPath);

            byte[] bytes = File.ReadAllBytes(glbPath);
            if (bytes.Length < 20)
                throw new InvalidDataException("GLB file is too small.");

            uint magic = BitConverter.ToUInt32(bytes, 0);
            if (magic != 0x46546C67)
                throw new InvalidDataException("Not a GLB file.");

            string json = null;
            byte[] binBytes = null;
            int offset = 12;
            while (offset + 8 <= bytes.Length)
            {
                int chunkLength = BitConverter.ToInt32(bytes, offset);
                uint chunkType = BitConverter.ToUInt32(bytes, offset + 4);
                offset += 8;
                if (offset + chunkLength > bytes.Length)
                    break;

                if (chunkType == 0x4E4F534A)
                    json = System.Text.Encoding.UTF8.GetString(bytes, offset, chunkLength);
                else if (chunkType == 0x004E4942)
                {
                    binBytes = new byte[chunkLength];
                    Array.Copy(bytes, offset, binBytes, 0, chunkLength);
                }

                offset += chunkLength;
            }

            if (string.IsNullOrEmpty(json) || binBytes == null)
                throw new InvalidDataException("GLB is missing JSON or BIN chunk.");

            var accessors = ParseAccessors(json);
            var bufferViews = ParseBufferViews(json);
            if (accessors.Count == 0 || bufferViews.Count == 0)
                throw new InvalidDataException("GLB is missing accessor metadata.");

            int positionAccessor = ExtractPrimitiveAccessorIndex(json, "POSITION");
            int indicesAccessor = ExtractPrimitiveAccessorIndex(json, "indices");
            if (positionAccessor < 0 || indicesAccessor < 0)
                throw new InvalidDataException("GLB primitive is missing POSITION or indices.");

            var positions = ReadVec3Accessor(accessors[positionAccessor], bufferViews, binBytes);
            var indices = ReadIndexAccessor(accessors[indicesAccessor], bufferViews, binBytes);

            var mesh = new Mesh
            {
                name = Path.GetFileNameWithoutExtension(glbPath)
            };

            if (positions.Length > 65535)
                mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;

            mesh.SetVertices(positions);
            mesh.SetTriangles(indices, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        static int ExtractPrimitiveAccessorIndex(string json, string key)
        {
            var meshMatch = Regex.Match(json, "\"meshes\"\\s*:\\s*\\[\\s*\\{[\\s\\S]*?\"primitives\"\\s*:\\s*\\[\\s*\\{([\\s\\S]*?)\\}\\s*\\]");
            if (!meshMatch.Success)
                return -1;

            string primitive = meshMatch.Groups[1].Value;
            if (key == "indices")
            {
                var match = Regex.Match(primitive, "\"indices\"\\s*:\\s*(\\d+)");
                return match.Success ? int.Parse(match.Groups[1].Value) : -1;
            }

            var attrMatch = Regex.Match(primitive, "\"attributes\"\\s*:\\s*\\{([\\s\\S]*?)\\}");
            if (!attrMatch.Success)
                return -1;

            var valueMatch = Regex.Match(attrMatch.Groups[1].Value, $"\"{key}\"\\s*:\\s*(\\d+)");
            return valueMatch.Success ? int.Parse(valueMatch.Groups[1].Value) : -1;
        }

        static List<Accessor> ParseAccessors(string json)
        {
            return ParseObjectArray(json, "\"accessors\"", obj => new Accessor
            {
                bufferView = ReadInt(obj, "bufferView"),
                byteOffset = ReadInt(obj, "byteOffset"),
                count = ReadInt(obj, "count"),
                componentType = ReadInt(obj, "componentType")
            });
        }

        static List<(int byteOffset, int byteStride)> ParseBufferViews(string json)
        {
            return ParseObjectArray(json, "\"bufferViews\"", obj =>
            {
                int stride = ReadInt(obj, "byteStride");
                if (stride <= 0)
                    stride = 12;
                return (ReadInt(obj, "byteOffset"), stride);
            });
        }

        static List<T> ParseObjectArray<T>(string json, string arrayKey, Func<string, T> parseItem)
        {
            var results = new List<T>();
            int keyIndex = json.IndexOf(arrayKey, StringComparison.Ordinal);
            if (keyIndex < 0)
                return results;

            int arrayStart = json.IndexOf('[', keyIndex);
            if (arrayStart < 0)
                return results;

            int i = arrayStart + 1;
            while (i < json.Length)
            {
                while (i < json.Length && char.IsWhiteSpace(json[i]))
                    i++;
                if (i >= json.Length || json[i] == ']')
                    break;
                if (json[i] != '{')
                {
                    i++;
                    continue;
                }

                int objStart = i;
                int depth = 0;
                for (; i < json.Length; ++i)
                {
                    if (json[i] == '{')
                        depth++;
                    else if (json[i] == '}')
                    {
                        depth--;
                        if (depth == 0)
                        {
                            i++;
                            break;
                        }
                    }
                }

                string obj = json.Substring(objStart, i - objStart);
                results.Add(parseItem(obj));
            }

            return results;
        }

        static int ReadInt(string obj, string key)
        {
            var match = Regex.Match(obj, $"\"{key}\"\\s*:\\s*(-?\\d+)");
            return match.Success ? int.Parse(match.Groups[1].Value) : 0;
        }

        static Vector3[] ReadVec3Accessor(Accessor accessor, List<(int byteOffset, int byteStride)> bufferViews, byte[] binBytes)
        {
            var view = bufferViews[accessor.bufferView];
            var result = new Vector3[accessor.count];
            int source = view.byteOffset + accessor.byteOffset;
            for (int i = 0; i < accessor.count; ++i)
            {
                int o = source + i * view.byteStride;
                result[i] = new Vector3(
                    BitConverter.ToSingle(binBytes, o),
                    BitConverter.ToSingle(binBytes, o + 4),
                    BitConverter.ToSingle(binBytes, o + 8));
            }

            return result;
        }

        static int[] ReadIndexAccessor(Accessor accessor, List<(int byteOffset, int byteStride)> bufferViews, byte[] binBytes)
        {
            var view = bufferViews[accessor.bufferView];
            var result = new int[accessor.count];
            int source = view.byteOffset + accessor.byteOffset;
            for (int i = 0; i < accessor.count; ++i)
            {
                result[i] = accessor.componentType switch
                {
                    5121 => binBytes[source + i],
                    5123 => BitConverter.ToUInt16(binBytes, source + i * 2),
                    5125 => (int)BitConverter.ToUInt32(binBytes, source + i * 4),
                    _ => throw new NotSupportedException($"Unsupported index component type {accessor.componentType}.")
                };
            }

            return result;
        }
    }
}
