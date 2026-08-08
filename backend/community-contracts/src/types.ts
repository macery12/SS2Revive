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
