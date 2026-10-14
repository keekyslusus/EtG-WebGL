// The format of audio.json, as port/audio/build_audio.py writes it and etg-audio.js reads it.
// Ids are Wwise's 32-bit ids (FNV-1 hashes of names); JSON object keys are their decimal strings.

/** [x, y, AkCurveInterpolation of the segment that starts here] */
type CurvePoint = [number, number, number];

/** An RTPC curve on a node, bus or attenuation: param is the AkPropID it drives (0 vol, 2 pitch...). */
interface WwRtpc {
  id: number;
  type: number;
  param: number;
  /** 2: the y values are dB stored as linear gain - 1 */
  scaling: number;
  points: CurvePoint[];
}

/** AkPropID values by name (see wwbank.PROPS); unnamed ones keep their number as the key. */
interface WwProps {
  vol?: number;
  pitch?: number;
  lpf?: number;
  hpf?: number;
  busvol?: number;
  makeup?: number;
  priority?: number;
  delay?: number;
  transition?: number;
  prob?: number;
  outbusvol?: number;
  loop?: number;
  idelay?: number;
  hdr_threshold?: number;
  hdr_ratio?: number;
  hdr_release?: number;
  [prop: string]: number | undefined;
}

/** Any object of the bank's hierarchy: sound, ranseq, switch, layer, mixer, bus, auxbus, state, attenuation,
 * mseg, mswitch, mranseq, mtrack. One shape with what each type adds as optional fields. */
interface WwNode {
  type: string;
  parent: number;
  /** output bus (the nearest node up the hierarchy that has one decides) */
  bus?: number;
  props?: WwProps;
  /** randomizers: [min, max] added to the property */
  ranged?: { [prop: string]: [number, number] };
  rtpc?: WwRtpc[];
  /** [state group, [[state, state object id]...]] */
  states?: [number, [number, number][]][];
  pos?: { "3d": boolean; att: number; pan?: false };
  /** max instances; bits & 1: kill the newest */
  max?: { n: number; bits: number };
  /** HDR envelope tracking: [overrides the parent's, enabled] */
  hdr_env?: [boolean, boolean];
  children?: number[];
  /** [index, effect id] */
  fx?: [number, number][];
  /** user aux busses it sends to */
  aux?: number[];

  // sound
  media?: number;

  // random/sequence container
  loop?: number;
  avoid?: number;
  /** 0 random, 1 sequence */
  mode?: number;
  /** 0 standard, 1 shuffle */
  random?: number;
  /** 1 use weights, 2 reset at each play, 8 continuous, 0x10 global */
  bits?: number;
  trans?: number[];
  trans_mode?: number;
  /** [child, weight] for ranseq; music playlist items for mranseq */
  playlist?: any[];

  // switch container
  /** 0 switch, 1 state */
  group_type?: number;
  group?: number;
  default?: number;
  switches?: { [value: string]: number[] };
  modes?: { [child: string]: number[] };

  // bus
  ducks?: any[];
  hdr?: { threshold: number; ratio: number; release: number; exp_release: boolean };

  // attenuation
  uses?: number[];
  curves?: { scaling: number; points: CurvePoint[] }[];

  // music: segment, switch, playlist, track
  grid?: [number, number];
  tempo?: number;
  sig?: [number, number];
  meter?: number;
  mflags?: number;
  stingers?: any[];
  duration?: number;
  /** [marker id, position ms, name] */
  markers?: [number, number, string][];
  rules?: any[];
  continue_playback?: number;
  tree_groups?: [number, number][];
  tree_mode?: number;
  tree?: [number, number, number, number][];
  sources?: number[];
  track_type?: number;
  /** [track, source, play_at, begin_trim, end_trim, src_duration] (ms) */
  clips?: [number, number, number, number, number, number][];
  subtracks?: number;
  lookahead?: number;
  clip_autos?: [number, number, CurvePoint[]][];
  switch?: any;
}

/** An action of an event: type is the high byte of the AkActionType (4 Play, 1 Stop, 0x12 SetState...). */
interface WwAction {
  type: number;
  scope: number;
  target: number;
  is_bus?: number;
  props?: WwProps;
  ranged?: { [prop: string]: [number, number] };
  curve?: number;
  group?: number;
  state?: number;
  switch?: number;
  meaning?: number;
  flags?: number;
  /** [value, random min, random max] */
  value?: [number, number, number];
  /** [object id, is bus] */
  except?: [number, number][];
}

/** An effect instance (FxShareSet / FxCustom) with what the engine needs of it. */
interface WwEffect {
  plugin: string;
  // parametric_eq
  bands?: { type: number; gain: number; freq: number; q: number; on: boolean }[];
  output?: number;
  // delay (time s, feedback/mix %); flanger (delay ms, levels, mix %)
  time?: number;
  feedback?: number;
  mix?: number;
  // measured: reverbs on sounds (measure_fx.py ir)
  ir?: string;
  ir_gain?: number;
  // measured models (measure_fx.py fit)
  model?: { hp_pre: number; k: number; knee: number; tone: number; hp_post: number; rect: number; dry: number; wet: number };
  delay?: number;
  dry?: number;
  ff?: number;
  fb?: number;
  wave?: number;
  center?: number;
  swing?: number;
  freq?: number;
  phase?: number;
  table?: number[];
  latency?: number;
  wet?: number;
  voices?: [number, number][];
  calib?: number;
}

interface WwMedia {
  rate: number;
  channels: number;
  samples: number;
  /** loop points in samples */
  loop?: [number, number];
  /** HDR loudness envelope: [[seconds, dB relative to the peak]...] */
  env?: [number, number][];
  /** music: its own file */
  file?: string;
  /** sound effects: [pack index, offset, length] */
  pack?: [number, number, number];
}

interface WwAudioData {
  version: number;
  settings: { volume_threshold?: number; max_voices?: number };
  effects: { [id: string]: WwEffect };
  /** bus id -> measured impulse response file */
  reverbs: { [bus: string]: string };
  packs: string[];
  media: { [id: string]: WwMedia };
  events: { [id: string]: WwAction[] };
  nodes: { [id: string]: WwNode };
  names: { [id: string]: string };
}
