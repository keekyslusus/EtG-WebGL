namespace WebShim
{
    public static class UnityNull
    {
        /// <summary>
        /// null for a destroyed or "fake null" object (Unity's == null), the object otherwise. Unity 6's editor hands out
        /// fake-null UnityEngine.Object placeholders for empty entries of object lists (Patcher, NormalizedUnityNullReturns).
        /// </summary>
        public static UnityEngine.Object Normalize(UnityEngine.Object obj)
        {
            return obj == null ? null : obj;
        }
    }
}
