﻿using Collada141;
using Nexus;
using PD2ModelParser.Sections;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using static PD2ModelParser.Tags;

namespace PD2ModelParser
{
    static class ColladaExporter
    {
        public static void ExportFile(FullModelData data, string path)
        {
            path = path.Replace(".model", ".dae");

            List<SectionHeader> sections = data.sections;
            Dictionary<UInt32, object> parsed_sections = data.parsed_sections;
            byte[] leftover_data = data.leftover_data;

            // Set up the XML structure
            library_geometries libgeoms = new library_geometries();

            library_visual_scenes libscenes = new library_visual_scenes();

            COLLADAScene scene = new COLLADAScene
            {
                instance_visual_scene = new InstanceWithExtra
                {
                    url = "#scene"
                }
            };

            COLLADA collada = new COLLADA
            {
                Items = new object[] { libgeoms, libscenes },
                scene = scene
            };

            // Build the mesh

            List<geometry> geometries = new List<geometry>();
            List<node> nodes = new List<node>();

            int model_id = 0;
            foreach (SectionHeader sectionheader in sections)
            {
                if (sectionheader.type == model_data_tag)
                {
                    Model model_data = (Model)parsed_sections[sectionheader.id];
                    if (model_data.version == 6)
                        continue;

                    geometry geom = SerializeModel(parsed_sections, model_data, model_id);

                    geometries.Add(geom);

                    nodes.Add(new node
                    {
                        id = "model-" + model_id,
                        name = "Model " + model_id,
                        type = NodeType.NODE,

                        instance_geometry = new instance_geometry[]
                        {
                            new instance_geometry
                            {
                                url = "#model-geom-" + model_id,
                                name = "Model Geom" + model_id
                            }
                        }
                    });

                    model_id++;
                }
            }

            libgeoms.geometry = geometries.ToArray();

            libscenes.visual_scene = new visual_scene[] { new visual_scene {
                id = "scene",
                name = "Scene",
                node = nodes.ToArray()
            } };

            Directory.CreateDirectory(Path.GetDirectoryName(path));

            using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            {
                collada.Save(fs);

                // Do we need this?
                // fs.Close();
            }
        }

        private static geometry SerializeModel(Dictionary<UInt32, object> parsed_sections, Model model_data, int id)
        {
            string VERT_ID = "vertices-" + id;
            string NORM_ID = "norms-" + id;
            string UV_ID = "uv-" + id;
            string RAW_VERT_ID = "vert_raw-" + id;

            PassthroughGP passthrough_section = (PassthroughGP)parsed_sections[model_data.passthroughGP_ID];
            Geometry geometry_section = (Geometry)parsed_sections[passthrough_section.geometry_section];
            Topology topology_section = (Topology)parsed_sections[passthrough_section.topology_section];

            int vertlen = geometry_section.verts.Count;
            int normlen = geometry_section.normals.Count;
            int uvlen = geometry_section.uvs.Count;

            triangles triangles = new triangles();

            List<InputLocalOffset> inputs = new List<InputLocalOffset>();

            inputs.Add(new InputLocalOffset
            {
                semantic = "VERTEX",
                source = "#" + VERT_ID,
                offset = 0,
            });

            if (normlen > 0)
            {
                inputs.Add(new InputLocalOffset
                {
                    semantic = "NORMAL",
                    source = "#" + NORM_ID,
                    offset = 0,
                });
            }

            if (uvlen > 0)
            {
                inputs.Add(new InputLocalOffset
                {
                    semantic = "TEXCOORD",
                    source = "#" + UV_ID,
                    offset = 0,
                    set = 1, // IDK what this does or why we need it
                });
            }

            triangles.input = inputs.ToArray();

            mesh mesh = new mesh
            {
                vertices = new vertices
                {
                    id = VERT_ID,
                    input = new InputLocal[] {
                        new InputLocal
                        {
                            semantic = "POSITION",
                            source = "#" + RAW_VERT_ID
                        }
                    }
                },
                Items = new object[] {
                    triangles
                }
            };

            triangles.p = "\n"; // Start on a newline

            foreach (Face face in topology_section.facelist)
            {
                if (!face.BoundsCheck(vertlen))
                {
                    throw new Exception("Vert Out Of Bounds!");
                }

                if (normlen > 0 && !face.BoundsCheck(normlen))
                {
                    throw new Exception("Norm Out Of Bounds!");
                }

                if (uvlen > 0 && !face.BoundsCheck(uvlen))
                {
                    throw new Exception("UV Out Of Bounds!");
                }

                triangles.p += face.a + " " + face.b + " " + face.c;  // Vertices

                triangles.p += "\n";

                triangles.count++;
            }

            List<source> sources = new List<source>();

            sources.Add(GenerateSource(RAW_VERT_ID, geometry_section.verts));

            if (normlen > 0)
                sources.Add(GenerateSource(NORM_ID, geometry_section.normals));

            if (uvlen > 0)
                sources.Add(GenerateSourceTex(UV_ID, geometry_section.uvs));

            mesh.source = sources.ToArray();

            return new geometry
            {
                name = "Diesel Converted Model " + id,
                id = "model-geom-" + id,
                Item = mesh
            };
        }

        private static source GenerateSource(string name, List<Vector3D> vecs)
        {
            return GenerateSource(name, new string[] { "X", "Y", "Z" }, vecs, VecToFloats);
        }

        private static source GenerateSource(string name, List<Vector2D> vecs)
        {
            return GenerateSource(name, new string[] { "X", "Y" }, vecs, VecToFloats);
        }

        private static source GenerateSourceTex(string name, List<Vector2D> vecs)
        {
            return GenerateSource(name, new string[] { "S", "T" }, vecs, VecToFloats);
        }

        private static double[] VecToFloats(Vector3D vec)
        {
            return new double[] { vec.X, vec.Y, vec.Z };
        }

        private static double[] VecToFloats(Vector2D vec)
        {
            return new double[] { vec.X, vec.Y };
        }

        private static source GenerateSource<T>(string id, string[] paramnames, List<T> list, Func<T, double[]> converter)
        {
            float_array verts = new float_array();
            source source = new source
            {
                id = id,
                name = id,
                Item = verts
            };

            List<double> values = new List<double>();

            int length = -1;

            foreach (T item in list)
            {
                double[] vals = converter(item);

                if (length == -1)
                {
                    length = vals.Length;
                }
                else if (vals.Length != length)
                {
                    throw new Exception("Incompatable lengths!");
                }

                values.AddRange(vals);
            }

            verts.Values = values.ToArray();
            verts.count = (ulong)verts.Values.LongLength;
            verts.id = id + "-data";
            verts.name = verts.id;

            param[] indexes = new param[paramnames.Length];

            for (int i = 0; i < paramnames.Length; i++)
            {
                indexes[i] = new param
                {
                    name = paramnames[i],
                    type = "float"
                };
            }

            source.technique_common = new sourceTechnique_common
            {
                accessor = new accessor
                {
                    source = "#" + verts.id,
                    count = (ulong)list.Count,
                    stride = (ulong)length,
                    param = indexes
                }
            };

            return source;
        }
    }
}
