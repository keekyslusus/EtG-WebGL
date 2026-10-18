// Stand-ins for UnityEngine types that exist on desktop but were removed from the WebGL player
// (legacy RakNet networking, MovieTexture, Substance ProceduralMaterial) or from Unity altogether (GUIText,
// GUITexture, GUIElement, GUILayer: Unity 6 keeps empty classes that aren't Components, which breaks
// PlayMaker's ComponentAction<GUIText>). EtG never uses them at runtime - they're only referenced by
// PlayMaker actions, iTween and camera setup code - but UnityLinker refuses to process an assembly that
// references a type it can't find, and the editor's Mono unloads it. The patcher retargets those type
// references from UnityEngine.* to WebShim, so the signatures below must match Unity's exactly
// (static vs instance, struct vs class, parameter types). Every member is a no-op.
//
// Type names deliberately shadow UnityEngine's (CS0436 is expected where the modules we compile
// against still have a type of that name).
#pragma warning disable 0436

using System;

namespace UnityEngine
{
    public enum NetworkConnectionError
    {
        InternalDirectConnectFailed = -5,
        EmptyConnectTarget = -4,
        IncorrectParameters = -3,
        CreateSocketOrThreadFailure = -2,
        AlreadyConnectedToAnotherServer = -1,
        NoError = 0,
        ConnectionFailed = 15,
        AlreadyConnectedToServer = 16,
        TooManyConnectedPlayers = 18,
        RSAPublicKeyMismatch = 21,
        ConnectionBanned = 22,
        InvalidPassword = 23,
        NATTargetNotConnected = 69,
        NATTargetConnectionLost = 71,
        NATPunchthroughFailed = 73,
    }

    public enum NetworkDisconnection
    {
        LostConnection = 20,
        Disconnected = 19,
    }

    public enum NetworkLogLevel
    {
        Off = 0,
        Informational = 1,
        Full = 3,
    }

    public enum NetworkPeerType
    {
        Disconnected = 0,
        Server = 1,
        Client = 2,
        Connecting = 3,
    }

    public enum MasterServerEvent
    {
        RegistrationFailedGameName = 0,
        RegistrationFailedGameType = 1,
        RegistrationFailedNoServer = 2,
        RegistrationSucceeded = 3,
        HostListReceived = 4,
    }

    public struct NetworkPlayer
    {
        public string ipAddress { get { return ""; } }
        public int port { get { return 0; } }
        public string guid { get { return ""; } }
        public string externalIP { get { return ""; } }
        public int externalPort { get { return 0; } }
    }

    public struct NetworkViewID
    {
    }

    public struct NetworkMessageInfo
    {
    }

    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    public sealed class RPC : Attribute
    {
    }

    public sealed class HostData
    {
        public bool useNat { get { return false; } }
        public string gameType { get { return ""; } }
        public string gameName { get { return ""; } }
        public int connectedPlayers { get { return 0; } }
        public int playerLimit { get { return 0; } }
        public string[] ip { get { return new string[0]; } }
        public int port { get { return 0; } }
        public bool passwordProtected { get { return false; } }
        public string comment { get { return ""; } }
        public string guid { get { return ""; } }
    }

    public sealed class BitStream
    {
        public bool isWriting { get { return false; } }
        public void Serialize(ref bool value) { }
        public void Serialize(ref char value) { }
        public void Serialize(ref int value) { }
        public void Serialize(ref float value) { }
        public void Serialize(ref Quaternion value) { }
        public void Serialize(ref Vector3 value) { }
    }

    public sealed class Network
    {
        public static string incomingPassword { set { } }
        public static NetworkLogLevel logLevel { set { } }
        public static NetworkPlayer[] connections { get { return new NetworkPlayer[0]; } }
        public static NetworkPlayer player { get { return default(NetworkPlayer); } }
        public static bool isClient { get { return false; } }
        public static bool isServer { get { return false; } }
        public static NetworkPeerType peerType { get { return NetworkPeerType.Disconnected; } }
        public static float sendRate { get { return 0f; } set { } }
        public static bool isMessageQueueRunning { get { return false; } set { } }
        public static double time { get { return 0.0; } }
        public static int minimumAllocatableViewIDs { get { return 0; } set { } }
        public static int maxConnections { get { return 0; } set { } }

        public static NetworkConnectionError InitializeServer(int connections, int listenPort, bool useNat) { return NetworkConnectionError.CreateSocketOrThreadFailure; }
        public static void InitializeSecurity() { }
        public static NetworkConnectionError Connect(string IP, int remotePort, string password) { return NetworkConnectionError.ConnectionFailed; }
        public static void Disconnect() { }
        public static void CloseConnection(NetworkPlayer target, bool sendDisconnectionNotification) { }
        public static void RemoveRPCs(NetworkPlayer playerToRemove) { }
        public static void RemoveRPCs(NetworkViewID viewID) { }
        public static void DestroyPlayerObjects(NetworkPlayer playerID) { }
        public static void SetLevelPrefix(int prefix) { }
        public static int GetLastPing(NetworkPlayer player) { return -1; }
        public static int GetAveragePing(NetworkPlayer player) { return -1; }
        public static bool HavePublicAddress() { return false; }
        public static Object Instantiate(Object prefab, Vector3 position, Quaternion rotation, int group) { return null; }
    }

    public sealed class MasterServer
    {
        public static string ipAddress { get { return ""; } set { } }
        public static int port { get { return 0; } set { } }
        public static int updateRate { get { return 0; } set { } }
        public static bool dedicatedServer { get { return false; } set { } }

        public static void RequestHostList(string gameTypeName) { }
        public static HostData[] PollHostList() { return new HostData[0]; }
        public static void RegisterHost(string gameTypeName, string gameName, string comment) { }
        public static void UnregisterHost() { }
        public static void ClearHostList() { }
    }

    public sealed class NetworkView : Behaviour
    {
        public NetworkViewID viewID { get { return default(NetworkViewID); } }
        public bool isMine { get { return false; } }
        public NetworkPlayer owner { get { return default(NetworkPlayer); } }
    }

    public sealed class MovieTexture : Texture
    {
        public AudioClip audioClip { get { return null; } }
        public bool loop { get { return false; } set { } }
        public void Play() { }
        public void Stop() { }
        public void Pause() { }
    }

    public sealed class ProceduralMaterial : Material
    {
        private ProceduralMaterial() : base((Material)null) { }

        public void SetProceduralFloat(string inputName, float inputValue) { }
        public void SetProceduralColor(string inputName, Color inputValue) { }
        public void SetProceduralVector(string inputName, Vector4 inputValue) { }
        public void SetProceduralBoolean(string inputName, bool inputValue) { }
        public void RebuildTextures() { }
        public void RebuildTexturesImmediately() { }
    }

    public class GUIElement : Behaviour
    {
        public bool HitTest(Vector3 screenPosition) { return false; }
        public bool HitTest(Vector3 screenPosition, Camera camera) { return false; }
        public Rect GetScreenRect() { return default(Rect); }
        public Rect GetScreenRect(Camera camera) { return default(Rect); }
    }

    public sealed class GUIText : GUIElement
    {
        public string text { get { return ""; } set { } }
        public Material material { get { return null; } set { } }
        public Vector2 pixelOffset { get { return default(Vector2); } set { } }
        public Font font { get { return null; } set { } }
        public TextAlignment alignment { get { return TextAlignment.Left; } set { } }
        public TextAnchor anchor { get { return TextAnchor.UpperLeft; } set { } }
        public float lineSpacing { get { return 1f; } set { } }
        public float tabSize { get { return 4f; } set { } }
        public int fontSize { get { return 0; } set { } }
        public FontStyle fontStyle { get { return FontStyle.Normal; } set { } }
        public bool richText { get { return false; } set { } }
        public Color color { get { return Color.white; } set { } }
    }

    public sealed class GUITexture : GUIElement
    {
        public Color color { get { return Color.white; } set { } }
        public Texture texture { get { return null; } set { } }
        public Rect pixelInset { get { return default(Rect); } set { } }
        public RectOffset border { get { return null; } set { } }
    }

    public sealed class GUILayer : Behaviour
    {
        public GUIElement HitTest(Vector3 screenPosition) { return null; }
    }
}
