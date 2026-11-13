using System.Collections.Generic;
using RealGlass.Glass;
using UnityEngine;

namespace RealGlass.Props
{
    /// <summary>
    /// The props another mod draws itself (2.0.0: Intersection Marking Tool's, PropGlassDraw.NoteImt),
    /// as drawn in the last frame -- their asset and matrix -- so the building picker can find them:
    /// they are none of the city's props, and no raycast of the game's meets them. Only while the
    /// picker is on (<see cref="Recording"/>); main thread.
    /// </summary>
    public static class OtherModsSeen
    {
        public struct Seen
        {
            public PropInfo Info;
            public Matrix4x4 Matrix;
        }

        /// <summary>Set by the picker while it is on: the draws are kept.</summary>
        public static bool Recording;

        private const int Most = 20000;
        private static List<Seen> _current = new List<Seen>();
        private static List<Seen> _last = new List<Seen>();
        private static int _frame = -1;
        private static readonly Dictionary<Mesh, KeyValuePair<Vector3[], int[]>> Shapes = new Dictionary<Mesh, KeyValuePair<Vector3[], int[]>>();

        public static void Add(PropInfo info, Matrix4x4 matrix)
        {
            int frame = Time.frameCount;
            if (frame != _frame)
            {
                List<Seen> swap = _last;
                _last = _current;
                _current = swap;
                _current.Clear();
                if (_frame != frame - 1)
                    _last.Clear();   // a frame or more without any: nothing of theirs is drawn now
                _frame = frame;
            }
            if (_current.Count < Most)
                _current.Add(new Seen { Info = info, Matrix = matrix });
        }

        /// <summary>The draws of the last whole frame (the picker asks before this frame's are made).</summary>
        private static List<Seen> LastFrame()
        {
            int frame = Time.frameCount;
            if (_frame == frame - 1)
                return _current;
            if (_frame == frame)
                return _last;
            return null;
        }

        /// <summary>Picker off: nothing kept, the shapes let go.</summary>
        public static void Stop()
        {
            Recording = false;
            _current.Clear();
            _last.Clear();
            Shapes.Clear();
            _frame = -1;
        }

        /// <summary>Each prop of theirs the ray meets (its box, then its triangles), with where, added to <paramref name="hits"/>.</summary>
        public static void RaycastAll(Ray ray, float length, List<KeyValuePair<Seen, float>> hits)
        {
            List<Seen> seen = LastFrame();
            if (seen == null)
                return;

            for (int i = 0; i < seen.Count; i++)
            {
                Seen s = seen[i];
                Mesh mesh = PerAsset.PropSelection.CanPick(s.Info) ? s.Info.m_mesh : null;   // no decal is ever picked
                if (mesh == null)
                    continue;

                // The ray in the mesh's own space; t stays the world ray's distance (the direction is
                // not normalised there).
                Matrix4x4 back = s.Matrix.inverse;
                Vector3 o = back.MultiplyPoint3x4(ray.origin);
                Vector3 d = back.MultiplyVector(ray.direction);
                Bounds bounds = mesh.bounds;
                float enter = ProceduralPick.RayBox(o, d, bounds.min, bounds.max);
                if (enter < 0f || enter >= length)
                    continue;

                KeyValuePair<Vector3[], int[]> shape;
                if (!Shapes.TryGetValue(mesh, out shape))
                {
                    if (Shapes.Count >= 64)
                        Shapes.Clear();
                    shape = new KeyValuePair<Vector3[], int[]>(mesh.vertices, mesh.triangles);
                    Shapes[mesh] = shape;
                }
                float t = ProceduralPick.RayTriangles(o, d, shape.Key, shape.Value, length);
                if (t >= 0f)
                    hits.Add(new KeyValuePair<Seen, float>(s, t));
            }
        }
    }
}
