// Globals the port's page scripts use that the DOM types don't have (npm run typecheck, tsconfig.json).

// Safari before 14.1, Chrome's prefixed fullscreen API (older iPad Safari) and the Keyboard Lock API.
interface Window {
  webkitAudioContext?: typeof AudioContext;
  /** port/site/etg-site.js */
  Etg: Record<string, any>;
  /** port/audio/etg-audio.js, called by EtgWebShim.jslib */
  EtgAudio: Record<string, any>;
  /** etg-build.js next to Build/ (EtgWebBuild.CopySite, port/site/install.py): the player's files */
  EtgBuild?: {
    loader: string;
    data: string;
    framework: string;
    code: string;
    companyName: string;
    productName: string;
    productVersion: string;
  };
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

/** Unity 6's WebGL loader (Build/<name>.loader.js), the parts the page uses. */
interface UnityInstance {
  Module: { canvas: HTMLCanvasElement; [key: string]: any };
  SetFullscreen(fullscreen: number): void;
  SendMessage(objectName: string, methodName: string, value?: string | number): void;
  Quit(): Promise<void>;
  [key: string]: any;
}

declare function createUnityInstance(
  canvas: HTMLCanvasElement,
  config: { dataUrl: string; frameworkUrl: string; codeUrl: string; [key: string]: any },
  onProgress?: (progress: number) => void,
): Promise<UnityInstance>;
