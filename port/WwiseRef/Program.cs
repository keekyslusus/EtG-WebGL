using System.Diagnostics;
using System.Text.Json;
using WwiseRef;

// Renders the audio differential test's cases (port/audio/audiodiff_cases.py) with the game's own Wwise.
//
//   WwiseRef <etg root> <cases.json> <out dir> [--only id,id,...] [--remove-fx bus:index,...]
//            [--remove-node-fx node:index,...]
//
// --remove-fx takes effects off busses (SetBusEffect with no shareset), e.g. to measure what a bus's
// effect chain does (port/audio/measure_ir.py); --remove-node-fx does the same for sounds and containers
// (SetActorMixerEffect; port/audio/measure_fx.py).
//
// Writes <out dir>/<case id>.wav (48 kHz, 16-bit stereo, Wwise's output capture of the master mix).
// The engine runs without its thread on a virtual clock (Clock.cs): each RenderAudio() renders one
// 1024-sample frame, so a case is rendered offline, frame by frame, with its steps applied at the frame
// they fall in. The output is suspended: nothing is played on the sound card.
if (args.Length < 3)
{
    Console.Error.WriteLine("usage: WwiseRef <etg root> <cases.json> <out dir> [--only id,id,...] [--remove-fx bus:index,...] [--remove-node-fx node:index,...]");
    return 2;
}
string root = Path.GetFullPath(args[0]);
string casesFile = Path.GetFullPath(args[1]);
string outDir = Path.GetFullPath(args[2]);
HashSet<string>? only = null;
List<(uint Id, uint Index)> FxList(string option)
{
    var list = new List<(uint, uint)>();
    int at = Array.IndexOf(args, option);
    if (at >= 0 && at + 1 < args.Length)
    {
        foreach (string item in args[at + 1].Split(','))
        {
            var parts = item.Split(':');
            list.Add((uint.Parse(parts[0]), uint.Parse(parts[1])));
        }
    }
    return list;
}
var removeFx = FxList("--remove-fx");
var removeNodeFx = FxList("--remove-node-fx");
int onlyAt = Array.IndexOf(args, "--only");
if (onlyAt >= 0 && onlyAt + 1 < args.Length)
{
    only = args[onlyAt + 1].Split(',').ToHashSet();
}
Directory.CreateDirectory(outDir);

string plugins = Path.Combine(root, "Enter the Gungeon", "EtG_Data", "Plugins");
string banks = Path.Combine(root, "Enter the Gungeon", "EtG_Data", "StreamingAssets", "Audio", "GeneratedSoundBanks", "Windows");
Ak.SetDllDirectory(plugins);
Clock.Install(Path.Combine(plugins, "AkSoundEngine.dll"));
string captureDir = Path.GetRelativePath(banks, outDir); // captures are written relative to the bank path

using var doc = JsonDocument.Parse(File.ReadAllBytes(casesFile));
var reset = doc.RootElement.GetProperty("reset");
uint[] stateGroups = reset.GetProperty("states").EnumerateArray().Select(e => e.GetUInt32()).ToArray();
var rtpcDefaults = reset.GetProperty("rtpcs").EnumerateArray().Select(e => (e[0].GetUInt32(), e[1].GetSingle())).ToArray();
var cases = doc.RootElement.GetProperty("cases").EnumerateArray().ToList();

const ulong Listener = 1;
const int Rate = 48000, Frame = 1024;

void Start()
{
    Ak.Init(plugins, banks);
    Ak.Check(Ak.CSharp_RegisterGameObjInternal(Listener), "register listener");
    Ak.Check(Ak.CSharp_AddDefaultListener(Listener), "add listener");
    Ak.CSharp_SetObjectPosition(Listener, 0, 0, 0, 0, 0, 1, 0, 1, 0);
    foreach (var (bus, index) in removeFx)
    {
        Ak.Check(Ak.CSharp_SetBusEffect__SWIG_0(bus, index, 0), $"SetBusEffect {bus}:{index}");
    }
    foreach (var (node, index) in removeNodeFx)
    {
        Ak.Check(Ak.CSharp_SetActorMixerEffect(node, index, 0), $"SetActorMixerEffect {node}:{index}");
    }
}

void RenderFrame()
{
    Clock.Advance(Frame, Rate);
    Ak.CSharp_RenderAudio__SWIG_1();
}

void Render(int frames)
{
    for (int i = 0; i < frames; i++)
    {
        RenderFrame();
    }
}

var sw = Stopwatch.StartNew();
Start();
int done = 0;
for (int index = 0; index < cases.Count; index++)
{
    var c = cases[index];
    string id = c.GetProperty("id").GetString()!;
    if (only != null && !only.Contains(id))
    {
        continue;
    }
    if (c.GetProperty("fresh").GetBoolean() && done > 0)
    {
        // sequence containers remember their position: start from a fresh engine like the browser does
        Ak.CSharp_Term();
        Start();
    }

    Ak.CSharp_StopAll__SWIG_1();
    Render(24); // let the previous case's tails and fades die out
    foreach (uint g in stateGroups)
    {
        Ak.CSharp_SetState__SWIG_0(g, 0);
    }
    foreach (var (rtpc, value) in rtpcDefaults)
    {
        Ak.CSharp_SetRTPCValue__SWIG_0(rtpc, value, Ak.InvalidGameObject, 0, 4, true);
    }
    Ak.CSharp_SetRandomSeed(c.GetProperty("seed").GetUInt32());
    ulong obj = 1000 + (ulong)index;
    Ak.Check(Ak.CSharp_RegisterGameObjInternal(obj), "register object");
    Ak.CSharp_SetObjectPosition(obj, 0, 0, 0, 0, 0, 1, 0, 1, 0);

    // The context at t = 0 (RTPCs, states, switches) is set before the capture starts: Wwise ramps bus
    // gains over a buffer or two, which would otherwise show up at the start of the case.
    var steps = c.GetProperty("steps").EnumerateArray().OrderBy(s => s.GetProperty("t").GetDouble()).ToList();
    foreach (var s in steps.Where(s => s.GetProperty("t").GetDouble() == 0 && s.GetProperty("op").GetString() != "postEvent").ToList())
    {
        Apply(s, obj);
        steps.Remove(s);
    }
    Render(4);

    double duration = c.GetProperty("duration").GetDouble();
    int frames = (int)Math.Ceiling(duration * Rate / Frame);
    Ak.Check(Ak.CSharp_StartOutputCapture(Path.Combine(captureDir, id + ".wav")), "StartOutputCapture");
    int next = 0;
    for (int f = 0; f < frames; f++)
    {
        double t = (double)f * Frame / Rate;
        while (next < steps.Count && steps[next].GetProperty("t").GetDouble() <= t + 1e-9)
        {
            Apply(steps[next++], obj);
        }
        RenderFrame();
    }
    Ak.CSharp_StopOutputCapture();
    Ak.CSharp_StopAll__SWIG_1();
    Render(2);
    Ak.CSharp_UnregisterGameObjInternal(obj);
    done++;
    if (done % 250 == 0)
    {
        Console.WriteLine($"   wwise: {done} cases, {sw.Elapsed.TotalSeconds:F0} s");
    }
}
Render(4);
Ak.CSharp_Term();
Console.WriteLine($"   wwise: {done} cases in {sw.Elapsed.TotalSeconds:F1} s");
return 0;

static void Apply(JsonElement step, ulong obj)
{
    string op = step.GetProperty("op").GetString()!;
    var a = step.GetProperty("args").EnumerateArray().ToArray();
    switch (op)
    {
        case "postEvent":
            Ak.CSharp_SetObjectPosition(obj, a[2].GetSingle(), a[3].GetSingle(), a[4].GetSingle(), 0, 0, 1, 0, 1, 0);
            if (Ak.CSharp_PostEvent__SWIG_5(a[0].GetUInt32(), obj) == 0)
            {
                Console.WriteLine($"   wwise: PostEvent {a[0].GetUInt32()} failed");
            }
            break;
        case "playNode":
        {
            // a sound or container on its own, without the event and the siblings around it (DynamicSequence)
            Ak.CSharp_SetObjectPosition(obj, a[2].GetSingle(), a[3].GetSingle(), a[4].GetSingle(), 0, 0, 1, 0, 1, 0);
            uint seq = Ak.CSharp_DynamicSequenceOpen__SWIG_3(obj);
            var playlist = Ak.CSharp_DynamicSequenceLockPlaylist(seq);
            Ak.Check(Ak.CSharp_AkPlaylist_Enqueue__SWIG_4(playlist, a[0].GetUInt32()), "Enqueue");
            Ak.CSharp_DynamicSequenceUnlockPlaylist(seq);
            Ak.Check(Ak.CSharp_DynamicSequencePlay__SWIG_2(seq), "DynamicSequence Play");
            Ak.CSharp_DynamicSequenceClose(seq);
            break;
        }
        case "setSwitch":
            Ak.CSharp_SetSwitch__SWIG_0(a[0].GetUInt32(), a[1].GetUInt32(), obj);
            break;
        case "setState":
            Ak.CSharp_SetState__SWIG_0(a[0].GetUInt32(), a[1].GetUInt32());
            break;
        case "setRtpc":
            // without Wwise's RTPC slew: a case's RTPC values are context, set before anything plays
            Ak.CSharp_SetRTPCValue__SWIG_0(a[0].GetUInt32(), a[1].GetSingle(), Ak.InvalidGameObject, 0, 4, true);
            break;
        case "stopAll":
            Ak.CSharp_StopAll__SWIG_1();
            break;
        default:
            throw new InvalidOperationException("unknown op " + op);
    }
}
