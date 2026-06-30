namespace IronNestVR
{
    /// <summary>
    /// Canonical co-op identity hash. WIRE PROTOCOL PRIMITIVE — both peers MUST derive identical ids from the
    /// same string (transform path, def id, valve id, etc.). This implementation must NEVER change once shipped;
    /// changing the constants or fold order would re-hash every networked id and desync every peer.
    /// FNV-1a 32-bit, deterministic across processes (unlike string.GetHashCode). Every subsystem that keys
    /// networked state by a path/name hash delegates here so the math cannot diverge between modules.
    /// </summary>
    internal static class CoopIds
    {
        public static int Fnv1A32(string s)
        {
            uint h = 2166136261u;
            for (int i = 0; i < s.Length; i++) { h ^= (byte)s[i]; h *= 16777619u; }
            return unchecked((int)h);
        }
    }
}
