// Globals the port's page scripts use that the DOM types don't have (npm run typecheck, tsconfig.json).

// Safari before 14.1, Chrome's prefixed fullscreen API (older iPad Safari) and the Keyboard Lock API.
interface Window {
  webkitAudioContext?: typeof AudioContext;
  /** port/site/etg-site.js */
  Etg: Record<string, any>;
  /** port/audio/etg-audio.js, called by EtgWebShim.jslib */
  EtgAudio: Record<string, any>;
}

interface Document {
  webkitFullscreenElement?: Element | null;
  webkitExitFullscreen?: () => Promise<void>;
}

interface HTMLElement {
  webkitRequestFullscreen?: (options?: FullscreenOptions) => Promise<void>;
}

interface Navigator {
  keyboard?: { lock(keyCodes?: string[]): Promise<void>; unlock(): void };
}

/** Unity 2017.4's WebGL loader (Build/UnityLoader.js), the parts the page uses. */
interface UnityInstance {
  Module: { canvas?: HTMLCanvasElement; [key: string]: any };
  [key: string]: any;
}

declare const UnityLoader: {
  instantiate(
    container: HTMLElement | string,
    url: string,
    options?: {
      onProgress?: (instance: UnityInstance, progress: number) => void;
      compatibilityCheck?: (instance: UnityInstance, ok: () => void, fail: () => void) => void;
      [key: string]: any;
    },
  ): UnityInstance;
  SystemInfo: { hasWebGL: number; [key: string]: any };
  Cryptography: { md5(data: Uint8Array): Uint8Array };
};
