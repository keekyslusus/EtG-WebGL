using UnityEngine;

namespace WebShim
{
    /// <summary>IMGUI members Unity 6 turned into stand-ins that throw; the patcher redirects calls here.</summary>
    public static class ImguiShim
    {
        /// <summary>
        /// Replaces TextEditor.content (NotImplementedException since Unity 2022: "use 'text'"). Mod the Gungeon
        /// API's SGUI reads it to draw its text fields (the F2 console).
        /// </summary>
        public static GUIContent GetContent(TextEditor editor)
        {
            return new GUIContent(editor.text);
        }
    }
}
