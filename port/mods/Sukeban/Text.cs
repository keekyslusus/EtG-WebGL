namespace Sukeban
{
    /// <summary>Item names and descriptions in English and Russian (the other languages fall back to English).</summary>
    internal static class Text
    {
        public static void Set(PickupObject item, string name, string shortDesc, string longDesc, string nameRu, string shortRu, string longRu)
        {
            item.SetName(name);
            item.SetShortDescription(shortDesc);
            item.SetLongDescription(longDesc);
            item.SetName(StringTableManager.GungeonSupportedLanguages.RUSSIAN, nameRu);
            item.SetShortDescription(StringTableManager.GungeonSupportedLanguages.RUSSIAN, shortRu);
            item.SetLongDescription(StringTableManager.GungeonSupportedLanguages.RUSSIAN, longRu);
        }
    }
}
