import { isCanonicalUuid } from "./validation";

const BASE64 = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

function guidBytes(levelId: string): Uint8Array | null {
  if (!isCanonicalUuid(levelId)) return null;
  const hex = levelId.replaceAll("-", "");
  const raw = new Uint8Array(16);
  for (let index = 0; index < raw.length; index += 1) {
    raw[index] = Number.parseInt(hex.slice(index * 2, index * 2 + 2), 16);
  }
  return new Uint8Array([
    raw[3]!, raw[2]!, raw[1]!, raw[0]!,
    raw[5]!, raw[4]!, raw[7]!, raw[6]!,
    raw[8]!, raw[9]!, raw[10]!, raw[11]!, raw[12]!, raw[13]!, raw[14]!, raw[15]!,
  ]);
}

export function levelCodeFromId(levelId: string): string {
  const bytes = guidBytes(levelId);
  if (bytes === null) return "";
  let output = "";
  for (let index = 0; index < bytes.length; index += 3) {
    const a = bytes[index] ?? 0;
    const b = bytes[index + 1] ?? 0;
    const c = bytes[index + 2] ?? 0;
    const remaining = bytes.length - index;
    output += BASE64[(a >> 2) & 63];
    output += BASE64[((a & 3) << 4) | (b >> 4)];
    output += remaining > 1 ? BASE64[((b & 15) << 2) | (c >> 6)] : "=";
    output += remaining > 2 ? BASE64[c & 63] : "=";
  }
  return output.slice(0, 22).replaceAll("/", "_").replaceAll("+", "-");
}
