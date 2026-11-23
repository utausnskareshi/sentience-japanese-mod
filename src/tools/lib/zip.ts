/**
 * 依存パッケージなしで ZIP ファイルを作る最小限の実装（Deflate 圧縮・UTF-8 ファイル名対応）。
 * 配布用 Zip の作成に使う。
 */
import { crc32, deflateRawSync } from 'node:zlib';

/** ZIP に入れる 1 ファイル */
export interface ZipEntry {
  /** ZIP 内のパス（"/" 区切り） */
  readonly name: string;
  readonly data: Buffer;
  /** 更新日時 */
  readonly modified: Date;
}

/**
 * Date を MS-DOS 形式の日付・時刻（各 16 ビット）に変換する（純粋関数）。
 * 日時の成分は UTC で取り出す。実行する PC のタイムゾーン（CI は UTC、手元は JST）に依らず同じ Zip にするため
 * （再現可能な配布物）。DOS 日時はタイムゾーンを持たず、展開ツールは現地時刻として解釈するので、
 * 展開後の更新日時には UTC の時刻がそのまま表示される。秒は 2 秒単位（仕様）。
 */
export const toDosDateTime = (date: Date): { readonly time: number; readonly day: number } => {
  const year = Math.max(1980, date.getUTCFullYear());
  return {
    time: (date.getUTCHours() << 11) | (date.getUTCMinutes() << 5) | Math.floor(date.getUTCSeconds() / 2),
    day: ((year - 1980) << 9) | ((date.getUTCMonth() + 1) << 5) | date.getUTCDate(),
  };
};

/**
 * ZIP ファイルの内容（バイト列）を作る（純粋関数）。
 * ファイル名は UTF-8（汎用フラグのビット 11）で格納するため、日本語名も文字化けしない。
 */
export const createZip = (entries: readonly ZipEntry[]): Buffer => {
  const locals: Buffer[] = [];
  const centrals: Buffer[] = [];
  let offset = 0;
  entries.forEach((entry) => {
    const name = Buffer.from(entry.name, 'utf8');
    const compressed = deflateRawSync(entry.data, { level: 9 });
    // 圧縮で小さくならない場合は無圧縮で格納する
    const useDeflate = compressed.length < entry.data.length;
    const body = useDeflate ? compressed : entry.data;
    const method = useDeflate ? 8 : 0;
    const crc = crc32(entry.data) >>> 0;
    const { time, day } = toDosDateTime(entry.modified);
    const flags = 0x0800; // UTF-8 ファイル名

    const local = Buffer.alloc(30);
    local.writeUInt32LE(0x04034b50, 0);
    local.writeUInt16LE(20, 4);
    local.writeUInt16LE(flags, 6);
    local.writeUInt16LE(method, 8);
    local.writeUInt16LE(time, 10);
    local.writeUInt16LE(day, 12);
    local.writeUInt32LE(crc, 14);
    local.writeUInt32LE(body.length, 18);
    local.writeUInt32LE(entry.data.length, 22);
    local.writeUInt16LE(name.length, 26);
    local.writeUInt16LE(0, 28);
    locals.push(local, name, body);

    const central = Buffer.alloc(46);
    central.writeUInt32LE(0x02014b50, 0);
    central.writeUInt16LE(20, 4);
    central.writeUInt16LE(20, 6);
    central.writeUInt16LE(flags, 8);
    central.writeUInt16LE(method, 10);
    central.writeUInt16LE(time, 12);
    central.writeUInt16LE(day, 14);
    central.writeUInt32LE(crc, 16);
    central.writeUInt32LE(body.length, 20);
    central.writeUInt32LE(entry.data.length, 24);
    central.writeUInt16LE(name.length, 28);
    central.writeUInt32LE(offset, 42);
    centrals.push(central, name);

    offset += local.length + name.length + body.length;
  });
  const centralSize = centrals.reduce((acc, b) => acc + b.length, 0);
  const end = Buffer.alloc(22);
  end.writeUInt32LE(0x06054b50, 0);
  end.writeUInt16LE(entries.length, 8);
  end.writeUInt16LE(entries.length, 10);
  end.writeUInt32LE(centralSize, 12);
  end.writeUInt32LE(offset, 16);
  return Buffer.concat([...locals, ...centrals, end]);
};
