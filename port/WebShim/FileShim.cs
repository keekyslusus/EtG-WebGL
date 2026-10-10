using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

namespace WebShim
{
    /// <summary>
    /// On WebGL, persistentDataPath lives in an in-memory FS backed by IndexedDB.
    /// Writes only reach IndexedDB after FS.syncfs, so the patcher wraps the
    /// file operations SaveManager finishes a save with (Move/Delete/Copy) and we
    /// flush after each one. The JS side debounces bursts of calls.
    /// </summary>
    public static class FileShim
    {
        [DllImport("__Internal")]
        private static extern void EtgSyncFS();

        public static void Sync()
        {
            if (Application.platform != RuntimePlatform.WebGLPlayer)
            {
                return;
            }
            EtgSyncFS();
        }

        public static void Move(string sourceFileName, string destFileName)
        {
            File.Move(sourceFileName, destFileName);
            Sync();
        }

        public static void Delete(string path)
        {
            File.Delete(path);
            Sync();
        }

        public static void Copy(string sourceFileName, string destFileName)
        {
            File.Copy(sourceFileName, destFileName);
            Sync();
        }

        public static void Copy(string sourceFileName, string destFileName, bool overwrite)
        {
            File.Copy(sourceFileName, destFileName, overwrite);
            Sync();
        }

        /// <summary>
        /// File.Create(path, bufferSize, options) is missing from some of Unity's 2.0 class library
        /// profiles. FileOptions (WriteThrough) is meaningless on the in-memory WebGL file system anyway.
        /// </summary>
        public static FileStream Create(string path, int bufferSize, FileOptions options)
        {
            return new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, bufferSize);
        }

        public static void WriteAllText(string path, string contents)
        {
            File.WriteAllText(path, contents);
            Sync();
        }

        public static void WriteAllBytes(string path, byte[] bytes)
        {
            File.WriteAllBytes(path, bytes);
            Sync();
        }
    }
}
