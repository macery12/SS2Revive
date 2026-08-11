export const API_VERSION = 1 as const;
export const MAP_FORMAT_VERSION = 29 as const;
export const MAX_BUNDLE_BYTES = 24 * 1024 * 1024;
export const MAX_CONTENT_BYTES = 4_096_000;
export const MAX_IMAGE_BYTES = 8 * 1024 * 1024;
export const MAX_MANIFEST_BYTES = 1024 * 1024;
export const MAX_TITLE_CHARACTERS = 128;
export const MAX_DESCRIPTION_CHARACTERS = 2048;
export const MAX_CREATORS = 4;
export const MAX_TAGS = 32;
export const MAX_METADATA_ITEM_CHARACTERS = 128;
export const MAX_PAGE_SIZE = 50;

/**
 * Serialized UTF-8 ceiling for the free-form manifest metadata that the catalogue echoes back
 * verbatim (tags, configurations, validations). The per-field count limits alone allow roughly
 * 131,000 characters of objectives per manifest, which a publisher could use to push a handful of
 * maps past the catalogue response budget and take the public listing offline for everyone. The
 * per-map ceiling keeps that cost predictable; the listing endpoints additionally degrade rather
 * than fail when the total budget is reached.
 */
export const MAX_STRUCTURED_METADATA_BYTES = 12 * 1024;

/** Response budget for the paged map listing. */
export const MAX_MAP_PAGE_BYTES = 1024 * 1024;

/** Response budget for the compatibility catalogue document. */
export const MAX_CATALOG_BYTES = 2 * 1024 * 1024;

export interface ApiErrorBody {
  error: {
    code: string;
    message: string;
    requestId: string;
  };
}

export interface MockSessionResponse {
  accessToken: string;
  expiresAtUtc: string;
  tokenType: "Bearer";
}

export interface DeviceSessionResponse {
  deviceSessionId: string;
  deviceSecret: string;
  userCode: string;
  activationPath: string;
  expiresAt: string;
  pollIntervalSeconds: number;
}

export interface SessionTokenResponse {
  tokenType: "Bearer";
  accessToken: string;
  expiresInSeconds: number;
  refreshToken: string;
  refreshExpiresInSeconds: number;
  scope: string;
}

export type UploadStatus =
  | "reserved"
  | "uploaded"
  | "validating"
  | "published"
  | "rejected"
  | "cancelled"
  | "expired";

export interface UploadReservationResponse {
  requestId: string;
  uploadId: string;
  status: UploadStatus;
  bundleUploadPath: string;
  statusPath: string;
  expiresAt: string;
}

export interface UploadStatusResponse {
  requestId: string;
  uploadId: string;
  status: UploadStatus;
  mapId?: string;
  revision?: number;
  errorCode?: string;
  errorMessage?: string;
}

export interface MapThumbnail {
  url: string;
  sizeBytes: number;
  sha256: string;
}

export interface CommunityMap {
  id: string;
  code: string;
  revision: number;
  title: string;
  description: string;
  creatorIds: string[];
  tags: string[];
  createdAtMs: number;
  updatedAtMs: number;
  clientVersion: 29;
  mapFormatVersion: 29;
  minimumReviveVersion: string;
  reviveVersion?: string;
  sizeBytes: number;
  sha256: string;
  configurations: unknown[];
  validations: unknown[];
  downloadUrl: string;
  thumbnail?: MapThumbnail;
}

export interface MapPage {
  items: CommunityMap[];
  nextCursor: string | null;
}

export interface MapVersionsResponse {
  mapId: string;
  versions: CommunityMap[];
}

export interface AuthPrincipal {
  steamId64: string;
  sessionId: string;
  scopes: readonly string[];
}
