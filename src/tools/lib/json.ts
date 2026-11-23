/**
 * JSON（unknown）を安全に読み取るための小さな補助関数群。
 * any を使わず、型の絞り込みに失敗したら理由つきの例外を投げる。
 */
import { readFileSync, writeFileSync, mkdirSync, renameSync, rmSync } from 'node:fs';
import { dirname } from 'node:path';

/** プレーンなオブジェクトか（配列・null を除く） */
export const isRecord = (value: unknown): value is Record<string, unknown> =>
  typeof value === 'object' && value !== null && !Array.isArray(value);

/** 配列か */
export const isArray = (value: unknown): value is unknown[] => Array.isArray(value);

/** オブジェクトであることを要求する */
export const asRecord = (value: unknown, where: string): Record<string, unknown> => {
  if (!isRecord(value)) {
    throw new Error(`${where}: オブジェクトを期待しましたが ${describe(value)} でした`);
  }
  return value;
};

/** 配列であることを要求する */
export const asArray = (value: unknown, where: string): unknown[] => {
  if (!isArray(value)) {
    throw new Error(`${where}: 配列を期待しましたが ${describe(value)} でした`);
  }
  return value;
};

/** 文字列であることを要求する */
export const asString = (value: unknown, where: string): string => {
  if (typeof value !== 'string') {
    throw new Error(`${where}: 文字列を期待しましたが ${describe(value)} でした`);
  }
  return value;
};

/** 数値であることを要求する */
export const asNumber = (value: unknown, where: string): number => {
  if (typeof value !== 'number') {
    throw new Error(`${where}: 数値を期待しましたが ${describe(value)} でした`);
  }
  return value;
};

/** 文字列なら返し、それ以外なら undefined（任意項目用） */
export const optString = (value: unknown): string | undefined => (typeof value === 'string' ? value : undefined);

/** 値の型を説明する（エラーメッセージ用） */
const describe = (value: unknown): string => {
  if (value === null) {
    return 'null';
  }
  if (Array.isArray(value)) {
    return '配列';
  }
  return typeof value;
};

/** 例外から Node のシステムエラーのコード（EPERM など）を取り出す（無ければ undefined） */
export const errorCode = (error: unknown): string | undefined => {
  if (typeof error === 'object' && error !== null && 'code' in error) {
    return typeof error.code === 'string' ? error.code : undefined;
  }
  return undefined;
};

/** 例外の内容を短く説明する（コードがあればコード、無ければメッセージ） */
const describeError = (error: unknown): string =>
  errorCode(error) ?? (error instanceof Error ? error.message : String(error));

/**
 * 厳密な UTF-8 の復号器。
 * readFileSync(path, 'utf8') は不正なバイト列（Shift_JIS や UTF-16 で保存したファイル）を黙って U+FFFD に
 * 置き換えるため、文字化けした訳文がそのまま取り込まれてしまう。fatal を指定して、不正なら例外にする。
 * 先頭の BOM（EF BB BF）は ignoreBOM: false（既定）により 1 つだけ取り除かれる。
 */
const STRICT_UTF8 = new TextDecoder('utf-8', { fatal: true });

/**
 * バイト列を厳密な UTF-8 として文字列にする（BOM 付きにも対応。BOM は取り除く）。
 * 不正な UTF-8 なら、場所（ファイル名など）と対処を含む例外を投げる。
 *
 * @param where エラーメッセージに含める説明（ファイルのパスなど）
 */
export const decodeUtf8 = (bytes: Uint8Array, where: string): string => {
  try {
    return STRICT_UTF8.decode(bytes);
  } catch (error) {
    // TextDecoder（fatal）は不正なバイト列で TypeError を投げる。それ以外は想定外なので、そのまま投げ直す
    if (error instanceof TypeError) {
      throw new Error(
        `${where} は UTF-8 として不正です。UTF-8（BOM なし可）で保存してください（Shift_JIS や UTF-16 で保存された可能性があります。PowerShell 5.1 の Set-Content・Out-File は既定で UTF-8 になりません）`,
        { cause: error },
      );
    }
    throw error;
  }
};

/**
 * JSON ファイルの内容（バイト列）を unknown として解析する（UTF-8 のみ。BOM 付きにも対応）。
 * 読み込んだバイト列をそのまま使いたい場合（変更の検出用に指紋も取る merge など）に使う。
 *
 * @param path エラーメッセージに含めるファイルのパス
 */
export const parseJsonBytes = (bytes: Uint8Array, path: string): unknown => {
  // BOM は decodeUtf8（TextDecoder）が取り除く。ここで重ねて取り除くと、本文先頭の U+FEFF まで消えてしまうため行わない
  const text = decodeUtf8(bytes, path);
  try {
    return JSON.parse(text) as unknown;
  } catch (error) {
    throw new Error(`JSON の解析に失敗しました: ${path}: ${error instanceof Error ? error.message : String(error)}`);
  }
};

/** JSON ファイルを unknown として読み込む（UTF-8 のみ。BOM 付きにも対応） */
export const readJson = (path: string): unknown => parseJsonBytes(readFileSync(path), path);

/**
 * rename の失敗のうち、一時的なものとして再試行するエラーコード。
 * Windows では、ウイルス対策・検索インデクサ・エディタなどが置き換え先のファイルを一瞬開いているだけで
 * EPERM・EACCES・EBUSY になることが多い（graceful-fs などが再試行するのと同じ理由）。
 */
const RETRYABLE_RENAME_CODES: ReadonlySet<string> = new Set(['EPERM', 'EACCES', 'EBUSY']);

/** rename を再試行するまでの待ち時間（ミリ秒）。要素数が再試行の回数で、合計で約 1.5 秒待つ */
export const RENAME_RETRY_DELAYS_MS: readonly number[] = [50, 100, 200, 400, 800];

/** rename の再試行で使う処理（テストで失敗を再現するために差し替えられる） */
export interface RenameOps {
  readonly rename: (from: string, to: string) => void;
  readonly sleep: (ms: number) => void;
}

/**
 * 同期的に待つ（再試行の間隔用）。作業用 CLI は同期処理のため、イベントループに戻らずに待つ。
 * Node.js ではメインスレッドでも Atomics.wait が使える。
 */
const sleepSync = (ms: number): void => {
  Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, ms);
};

/** 既定の rename の処理（実際のファイル操作と同期的な待ち） */
const DEFAULT_RENAME_OPS: RenameOps = { rename: renameSync, sleep: sleepSync };

/**
 * rename を行う。一時的な失敗（EPERM・EACCES・EBUSY）のときだけ、delaysMs の間隔をあけて再試行する。
 * それ以外のエラー、または再試行を使い切った場合は、最後の例外をそのまま投げる。
 */
export const renameWithRetry = (
  from: string,
  to: string,
  delaysMs: readonly number[] = RENAME_RETRY_DELAYS_MS,
  ops: RenameOps = DEFAULT_RENAME_OPS,
): void => {
  for (let attempt = 0; ; attempt++) {
    try {
      ops.rename(from, to);
      return;
    } catch (error) {
      const code = errorCode(error);
      const delay = delaysMs[attempt];
      if (code === undefined || !RETRYABLE_RENAME_CODES.has(code) || delay === undefined) {
        throw error;
      }
      ops.sleep(delay);
    }
  }
};

/** 書き込みの設定（すべて任意） */
export interface AtomicWriteOptions {
  /** rename を再試行する間隔（テスト用に差し替えられる。既定は RENAME_RETRY_DELAYS_MS） */
  readonly retryDelaysMs?: readonly number[] | undefined;
  /** rename の処理（テスト用に差し替えられる） */
  readonly ops?: RenameOps | undefined;
  /**
   * 一時ファイルを書き終えてから置き換える直前に呼ぶ確認（例外を投げると、置き換えずに一時ファイルを削除し、
   * その例外をそのまま投げる）。merge が「読み込み後に他のプロセスが変えていないか」を置き換えの直前に確かめるのに使う。
   */
  readonly beforeReplace?: (() => void) | undefined;
}

/**
 * テキストファイルを書き出す（親フォルダは自動作成。UTF-8）。
 * 途中で中断しても壊れたファイルが残らないよう、一時ファイルに書いてから rename で置き換える。
 * rename の一時的な失敗は再試行し（renameWithRetry）、最終的に失敗したら一時ファイルを削除してから、
 * 対象のパスと原因（エラーコード）を含む例外を投げる（元の例外は cause に残す）。
 */
export const writeTextAtomic = (path: string, text: string, options: AtomicWriteOptions = {}): void => {
  mkdirSync(dirname(path), { recursive: true });
  const temp = `${path}.${process.pid}.tmp`;
  /** 一時ファイルを削除する。削除にも失敗した場合は、その旨の補足を返す（握りつぶさずメッセージに含める） */
  const removeTemp = (): string => {
    try {
      rmSync(temp, { force: true });
      return '';
    } catch (cleanupError) {
      return `。一時ファイル ${temp} も削除できませんでした（${describeError(cleanupError)}）`;
    }
  };
  try {
    writeFileSync(temp, text, 'utf8');
  } catch (error) {
    throw new Error(`${path} を書き込めませんでした（${describeError(error)}）${removeTemp()}`, { cause: error });
  }
  if (options.beforeReplace !== undefined) {
    try {
      options.beforeReplace();
    } catch (error) {
      // 確認で止めた場合は置き換えない。確認の例外は呼び出し側が意味を知っているので、包まずにそのまま投げる
      const note = removeTemp();
      throw note === '' ? error : new Error(`${error instanceof Error ? error.message : String(error)}${note}`, { cause: error });
    }
  }
  try {
    renameWithRetry(temp, path, options.retryDelaysMs ?? RENAME_RETRY_DELAYS_MS, options.ops ?? DEFAULT_RENAME_OPS);
  } catch (error) {
    throw new Error(`${path} を書き込めませんでした（${describeError(error)}）${removeTemp()}`, { cause: error });
  }
};

/**
 * JSON ファイルを書き出す（親フォルダは自動作成。末尾改行あり、LF）。
 * 一時ファイルに書いてから置き換える（writeTextAtomic）ため、途中で中断しても壊れたファイルは残らない。
 */
export const writeJson = (path: string, value: unknown, indent = 2, options: AtomicWriteOptions = {}): void => {
  writeTextAtomic(path, `${JSON.stringify(value, null, indent)}\n`, options);
};
