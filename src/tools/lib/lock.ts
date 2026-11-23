/**
 * 同時実行を防ぐロックファイル（merge の排他に使う）。
 *
 * openSync(path, 'wx')（ファイルが既にあれば EEXIST で失敗する、排他的な作成）でロックファイルを作り、
 * 作れたら解放用の関数を返す。既にあれば、別のプロセスが実行中か、前回の実行が異常終了して残ったものとみなし、
 * ロックファイルの場所（と、書かれていれば作成したプロセスの情報）を含む例外を投げる。
 * ロックファイルには作成したプロセスの PID と時刻を書く（残ったものかどうかを利用者が判断できるように）。
 */
import { closeSync, mkdirSync, openSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { dirname } from 'node:path';
import { errorCode } from './json.ts';

/**
 * ロックファイルに書かれた情報（メッセージの補足用）。
 * 読めない場合（作成直後でまだ空・直後に削除された等）も本来の案内を続けられるよう、読めなかった理由を返す。
 */
const readLockInfo = (path: string): string => {
  try {
    return readFileSync(path, 'utf8').trim();
  } catch (error) {
    return `（読み取れませんでした: ${errorCode(error) ?? (error instanceof Error ? error.message : String(error))}）`;
  }
};

/**
 * ロックを取る。取れたら解放用の関数（ロックファイルを削除する）を返す。
 *
 * @param path ロックファイルのパス
 * @param label 排他する処理の名前（メッセージ用。例: "merge"）
 */
export const acquireLock = (path: string, label: string): (() => void) => {
  mkdirSync(dirname(path), { recursive: true });
  const fd = ((): number => {
    try {
      return openSync(path, 'wx');
    } catch (error) {
      if (errorCode(error) === 'EEXIST') {
        const info = readLockInfo(path);
        throw new Error(
          `別の ${label} が実行中です（異常終了で残った場合は ${path} を削除してください）${info === '' ? '' : `。ロックの情報: ${info}`}`,
          { cause: error },
        );
      }
      throw error;
    }
  })();
  try {
    writeFileSync(fd, `pid=${process.pid} started=${new Date().toISOString()}\n`, 'utf8');
  } catch (error) {
    // 情報を書けなかった場合も、ロックファイルを残さない（残すと次回の実行が止まってしまう）
    closeSync(fd);
    rmSync(path, { force: true });
    throw error;
  }
  closeSync(fd);
  return () => rmSync(path, { force: true });
};
