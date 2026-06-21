using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Logging;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace IronNestVR
{
    /// <summary>
    /// Pulls "uniform" props out of the GAME'S OWN loaded assets — the gas mask and the Castile flag — and
    /// builds clean, render-only copies to hang on the remote teammate's avatar (see <see cref="RemoteAvatar"/>).
    ///
    /// We never clone the source GameObject wholesale (that would drag along its scripts / animator / armature);
    /// instead we read the matched renderer's MESH + MATERIALS and rebuild a fresh MeshFilter+MeshRenderer, so the
    /// prop is inert and rides wherever the avatar poses it. A skinned source mesh draws in bind pose, which is
    /// exactly what we want for a rigid prop. If only a texture matches (e.g. the flag turns out to be a 2D banner
    /// rather than a mesh) we fall back to a textured double-sided quad.
    ///
    /// Matching is by NORMALISED name tokens (lowercased, alphanumerics only, AND-matched) so "gas mask",
    /// "gas mask.003", "GasMask" and material "gas_mask_BaseColor" all match ["gas","mask"]. If nothing matches we
    /// dump the loaded candidates ONCE so the exact in-game names show up in the BepInEx log for the next pass.
    /// </summary>
    internal static class AvatarProps
    {
        private static ManualLogSource Log => Plugin.Logger;
        private static readonly HashSet<string> _dumped = new HashSet<string>();

        // Build a render-only prop from loaded game assets. Returns null if nothing matching is resident yet
        // (the caller retries on a timer). fallbackTint colours the texture-quad path when no material is found.
        public static GameObject Build(string label, string[] tokens, Color fallbackTint)
        {
            try
            {
                var go = FromRenderer(label, tokens);
                if (go == null) go = FromTexture(label, tokens, fallbackTint);
                if (go == null) { DumpOnce(label, tokens); return null; }
                return go;
            }
            catch (Exception e) { Log.LogWarning($"[prop] {label}: build failed: {e.Message}"); return null; }
        }

        // ---------------- 3D mesh path (preferred) ----------------

        private static GameObject FromRenderer(string label, string[] tokens)
        {
            var g = ScanMesh(label, tokens, false);     // static meshes first
            if (g == null) g = ScanMesh(label, tokens, true);   // then skinned
            return g;
        }

        private static GameObject ScanMesh(string label, string[] tokens, bool skinned)
        {
            var arr = skinned
                ? Resources.FindObjectsOfTypeAll(Il2CppType.Of<SkinnedMeshRenderer>())
                : Resources.FindObjectsOfTypeAll(Il2CppType.Of<MeshRenderer>());
            if (arr == null) return null;
            for (int i = 0; i < arr.Length; i++)
            {
                Renderer src = null; Mesh mesh = null;
                try
                {
                    if (skinned)
                    {
                        var s = arr[i].TryCast<SkinnedMeshRenderer>(); if (s == null) continue;
                        src = s; mesh = s.sharedMesh;
                    }
                    else
                    {
                        var r = arr[i].TryCast<MeshRenderer>(); if (r == null) continue;
                        src = r;
                        var mf = r.gameObject.GetComponent<MeshFilter>(); if (mf != null) mesh = mf.sharedMesh;
                    }
                }
                catch { continue; }
                if (mesh == null || src == null) continue;
                if (!NameMatches(src, mesh, tokens)) continue;

                var built = BuildFromMesh(label, mesh, src);
                if (built != null)
                {
                    Log.LogInfo($"[prop] {label} = mesh '{SafeName(mesh)}' on '{SafeName(src.gameObject)}' ({(skinned ? "skinned→bind" : "static")})");
                    return built;
                }
            }
            return null;
        }

        private static GameObject BuildFromMesh(string label, Mesh mesh, Renderer src)
        {
            var go = new GameObject("CoopProp_" + label);
            var mf = go.AddComponent(Il2CppType.Of<MeshFilter>()).TryCast<MeshFilter>();
            if (mf == null) { UnityEngine.Object.Destroy(go); return null; }
            mf.sharedMesh = mesh;
            var mr = go.AddComponent(Il2CppType.Of<MeshRenderer>()).TryCast<MeshRenderer>();
            if (mr == null) { UnityEngine.Object.Destroy(go); return null; }
            // Reuse the game's own (already-URP) materials so the prop keeps its real textures.
            try { var mats = src.sharedMaterials; if (mats != null && mats.Length > 0) mr.sharedMaterials = mats; } catch { }
            mr.enabled = true;
            return go;
        }

        private static bool NameMatches(Renderer src, Mesh mesh, string[] tokens)
        {
            try { if (Match(SafeName(src.gameObject), tokens)) return true; } catch { }
            if (Match(SafeName(mesh), tokens)) return true;
            try
            {
                var mats = src.sharedMaterials;
                if (mats != null) for (int i = 0; i < mats.Length; i++) { var m = mats[i]; if (m != null && Match(SafeName(m), tokens)) return true; }
            }
            catch { }
            return false;
        }

        // ---------------- cape (curved cloth that wraps the shoulders) ----------------

        // Build a draping cape instead of a flat banner: a procedurally generated curved sheet that arcs around
        // the back of the shoulders (radius chosen to clear the ~0.17 m torso so it never cuts into the body) and
        // falls to a wider hem. The matched flag's TEXTURE is mapped across it, so the Castile flag reads as the
        // cape's cloth. If no texture is found we still build a cloth-tinted cape (and dump candidates once), so
        // the shape is right even before the texture is wired. Local origin sits at the collar (top-centre), on
        // the body's vertical axis, so posing it at the shoulders drapes it down and around.
        public static GameObject BuildCape(string label, string[] tokens, Color cloth)
        {
            try
            {
                Texture tex = FindTextureForTokens(tokens);
                if (tex == null) DumpOnce(label, tokens);

                const int cols = 20;          // segments around the shoulder arc
                const int rows = 12;          // segments down the drape
                const float arcDeg = 200f;    // wrap angle (open at the front)
                const float rTop = 0.20f;     // collar radius — clears the ~0.17 m torso capsule
                const float rBot = 0.30f;     // hem flares out
                const float length = 0.55f;   // drop from collar to hem (metres, before CoopFlagScale)
                float halfArc = arcDeg * 0.5f * Mathf.Deg2Rad;
                int stride = cols + 1;

                int vcount = stride * (rows + 1);
                var verts = new Il2CppStructArray<Vector3>(vcount);
                var uvs = new Il2CppStructArray<Vector2>(vcount);
                int vi = 0;
                for (int r = 0; r <= rows; r++)
                {
                    float v = (float)r / rows;                    // 0 collar → 1 hem
                    float rad = Mathf.Lerp(rTop, rBot, v);
                    float y = -length * v;
                    for (int c = 0; c <= cols; c++)
                    {
                        float u = (float)c / cols;                // 0..1 across the arc
                        float ang = Mathf.Lerp(-halfArc, halfArc, u);
                        verts[vi] = new Vector3(rad * Mathf.Sin(ang), y, -rad * Mathf.Cos(ang));  // ang 0 = straight back
                        uvs[vi] = new Vector2(u, 1f - v);
                        vi++;
                    }
                }

                var tris = new Il2CppStructArray<int>(cols * rows * 6);
                int ti = 0;
                for (int r = 0; r < rows; r++)
                    for (int c = 0; c < cols; c++)
                    {
                        int i0 = r * stride + c, i1 = i0 + 1, i2 = i0 + stride, i3 = i2 + 1;
                        tris[ti++] = i0; tris[ti++] = i2; tris[ti++] = i1;
                        tris[ti++] = i1; tris[ti++] = i2; tris[ti++] = i3;
                    }

                var mesh = new Mesh();
                mesh.vertices = verts;
                mesh.uv = uvs;
                mesh.triangles = tris;
                mesh.RecalculateNormals();
                mesh.RecalculateBounds();

                var go = new GameObject("CoopProp_" + label);
                var mf = go.AddComponent(Il2CppType.Of<MeshFilter>()).TryCast<MeshFilter>();
                if (mf == null) { UnityEngine.Object.Destroy(go); return null; }
                mf.sharedMesh = mesh;
                var mr = go.AddComponent(Il2CppType.Of<MeshRenderer>()).TryCast<MeshRenderer>();
                if (mr == null) { UnityEngine.Object.Destroy(go); return null; }

                var sh = Shader.Find("Universal Render Pipeline/Unlit");
                if (sh == null) sh = Shader.Find("Universal Render Pipeline/Lit");
                if (sh == null) sh = Shader.Find("Sprites/Default");
                var mat = new Material(sh);
                if (tex != null)
                {
                    try { mat.mainTexture = tex; } catch { }
                    try { if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", tex); } catch { }
                    try { if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", Color.white); } catch { }
                }
                else
                {
                    try { mat.color = cloth; } catch { }
                    try { if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", cloth); } catch { }
                }
                try { if (mat.HasProperty("_Cull")) mat.SetFloat("_Cull", 0f); } catch { }   // double-sided cloth
                mr.material = mat;

                Log.LogInfo($"[prop] {label} = cape (curved cloth, {(tex != null ? "texture '" + SafeName(tex) + "'" : "tinted — no texture found")})");
                return go;
            }
            catch (Exception e) { Log.LogWarning($"[prop] {label}: cape build failed: {e.Message}"); return null; }
        }

        // Find the flag's image: a Texture2D whose name matches, else a matching Material's main texture.
        private static Texture FindTextureForTokens(string[] tokens)
        {
            try
            {
                var arr = Resources.FindObjectsOfTypeAll(Il2CppType.Of<Texture2D>());
                if (arr != null) for (int i = 0; i < arr.Length; i++)
                {
                    var t = arr[i].TryCast<Texture2D>(); if (t == null) continue;
                    if (Match(SafeName(t), tokens)) return t;
                }
            }
            catch { }
            try
            {
                var arr = Resources.FindObjectsOfTypeAll(Il2CppType.Of<Material>());
                if (arr != null) for (int i = 0; i < arr.Length; i++)
                {
                    var m = arr[i].TryCast<Material>(); if (m == null) continue;
                    if (!Match(SafeName(m), tokens)) continue;
                    Texture t = null;
                    try { t = m.mainTexture; } catch { }
                    try { if (t == null && m.HasProperty("_BaseMap")) t = m.GetTexture("_BaseMap"); } catch { }
                    if (t != null) return t;
                }
            }
            catch { }
            return null;
        }

        // ---------------- texture path (fallback, e.g. a 2D flag) ----------------

        private static GameObject FromTexture(string label, string[] tokens, Color tint)
        {
            Texture2D tex = null;
            try
            {
                var arr = Resources.FindObjectsOfTypeAll(Il2CppType.Of<Texture2D>());
                if (arr != null) for (int i = 0; i < arr.Length; i++)
                {
                    var t = arr[i].TryCast<Texture2D>(); if (t == null) continue;
                    if (Match(SafeName(t), tokens)) { tex = t; break; }
                }
            }
            catch { }
            if (tex == null) return null;

            var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
            try { var col = q.GetComponent<Collider>(); if (col != null) UnityEngine.Object.Destroy(col); } catch { }
            var sh = Shader.Find("Universal Render Pipeline/Unlit");
            if (sh == null) sh = Shader.Find("Universal Render Pipeline/Lit");
            if (sh == null) sh = Shader.Find("Sprites/Default");
            var mat = new Material(sh);
            try { mat.mainTexture = tex; } catch { }
            try { if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", tex); } catch { }
            try { if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", Color.white); } catch { }
            try { if (mat.HasProperty("_Cull")) mat.SetFloat("_Cull", 0f); } catch { }   // double-sided
            try { q.GetComponent<MeshRenderer>().material = mat; } catch { }

            float aspect = 1f;
            try { if (tex.height > 0) aspect = (float)tex.width / tex.height; } catch { }
            q.transform.localScale = new Vector3(aspect, 1f, 1f);   // intrinsic; the avatar scales this uniformly
            Log.LogInfo($"[prop] {label} = texture '{SafeName(tex)}' → quad (aspect {aspect:F2})");
            return q;
        }

        // ---------------- diagnostics ----------------

        // Log loaded objects whose name contains ANY token, so the real in-game names are visible if our guess
        // missed. Once per label (it scans every loaded object of each type — not something to repeat each tick).
        private static void DumpOnce(string label, string[] tokens)
        {
            if (_dumped.Contains(label)) return;
            _dumped.Add(label);
            Log.LogWarning($"[prop] {label}: no asset matched tokens [{string.Join(",", tokens)}] — loaded candidates:");
            int n = 0;
            n += DumpType<GameObject>("GameObject", tokens);
            n += DumpType<Mesh>("Mesh", tokens);
            n += DumpType<Texture2D>("Texture2D", tokens);
            n += DumpType<Material>("Material", tokens);
            if (n == 0) Log.LogWarning($"[prop] {label}: nothing loaded contains any of [{string.Join(",", tokens)}] — asset not resident in this scene");
        }

        private static int DumpType<T>(string typeName, string[] tokens) where T : UnityEngine.Object
        {
            int shown = 0;
            try
            {
                var arr = Resources.FindObjectsOfTypeAll(Il2CppType.Of<T>());
                if (arr != null) for (int i = 0; i < arr.Length && shown < 14; i++)
                {
                    var o = arr[i].TryCast<T>(); if (o == null) continue;
                    string nm = SafeName(o); string norm = Norm(nm);
                    bool any = false; for (int k = 0; k < tokens.Length; k++) if (norm.Contains(tokens[k])) { any = true; break; }
                    if (!any) continue;
                    Log.LogWarning($"[prop]    {typeName}: '{nm}'");
                    shown++;
                }
            }
            catch { }
            return shown;
        }

        // ---------------- name matching ----------------

        private static string SafeName(UnityEngine.Object o)
        {
            try { return o != null ? o.name : ""; } catch { return ""; }
        }

        private static string Norm(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++) { char c = s[i]; if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c)); }
            return sb.ToString();
        }

        private static bool Match(string name, string[] tokens)
        {
            var n = Norm(name);
            if (n.Length == 0) return false;
            for (int i = 0; i < tokens.Length; i++) if (!n.Contains(tokens[i])) return false;
            return true;
        }
    }
}
