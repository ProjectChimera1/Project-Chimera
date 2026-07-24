// Minimal ambient declaration of the Nakama runtime type surface used by this module (Story 9.12). The full official
// types ship with Nakama; this local shim declares only what src/main.ts references, so `tsc --noEmit` typechecks
// without a network dependency. These types are `import type`-only and are erased by the bundler at build time.

declare module 'nkruntime' {
  export interface Context {
    userId: string;
  }

  export interface Logger {
    info(format: string, ...args: any[]): void;
    warn(format: string, ...args: any[]): void;
    error(format: string, ...args: any[]): void;
  }

  export interface StorageWriteRequest {
    collection: string;
    key: string;
    userId?: string;
    value: { [key: string]: any };
    version?: string;
    permissionRead?: number;
    permissionWrite?: number;
  }

  export interface StorageWriteAck {
    collection: string;
    key: string;
    userId: string;
    version: string;
  }

  export interface StorageReadRequest {
    collection: string;
    key: string;
    userId?: string;
  }

  export interface StorageObject {
    collection: string;
    key: string;
    userId: string;
    version: string;
    value: { [key: string]: any };
  }

  export interface Nakama {
    storageWrite(writes: StorageWriteRequest[]): StorageWriteAck[];
    storageRead(reads: StorageReadRequest[]): StorageObject[];
  }

  export type RpcFunction = (ctx: Context, logger: Logger, nk: Nakama, payload: string) => string;

  export interface Initializer {
    registerRpc(id: string, func: RpcFunction): void;
  }
}
