/// <reference types="vite/client" />

interface ImportMetaEnv {
  /** Base URL do SimCore. Default: http://localhost:8099 */
  readonly VITE_SIM_API?: string;
  /** Token Bearer opcional (somente dev). */
  readonly VITE_SIM_TOKEN?: string;
}

interface ImportMeta {
  readonly env: ImportMetaEnv;
}
