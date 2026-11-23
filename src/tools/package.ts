/**
 * 配布用 Zip を作る CLI。
 *
 * 使い方: npm run package
 * 前提:   npm run build で dist/payload を作り、patcher\build.ps1 で dist/publish/SentienceJP-Patcher.exe を作成済みであること
 * 入力:   release/ フォルダ内の同梱テキスト（RELEASE_TEXTS に列挙したものだけ）
 * 出力:   dist/release/SentienceJP-Patcher-v<バージョン>.zip と SHA256SUMS.txt（Zip のハッシュ値）
 *
 * Zip を作る前に、exe を自己検査モード（--verify-payload）で起動し、埋め込まれたペイロード（manifest.json の SHA-256）と
 * パッチの版が、現在の dist/payload/manifest.json・package.json と一致することを確かめる（一致しなければ作らない）。
 * Zip の各ファイルの更新日時は SOURCE_DATE_EPOCH → git のコミット時刻 → 現在時刻（警告）の順に決め、
 * 同じ入力からは同じ Zip（同じ SHA-256）ができるようにする。判定の規則は lib/package-lib.ts（単体テストあり）。
 */
import { createHash } from 'node:crypto';
import { execFileSync, spawnSync } from 'node:child_process';
import { existsSync, mkdtempSync, readFileSync, rmSync, writeFileSync, mkdirSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { asRecord, asString, decodeUtf8, readJson } from './lib/json.ts';
import { createZip, type ZipEntry } from './lib/zip.ts';
import { parseCliArgs } from './lib/cli.ts';
import { PAYLOAD_DIR } from './lib/payload.ts';
import {
  EXE_MISMATCH_GUIDANCE,
  VERIFY_PAYLOAD_SWITCH,
  chooseZipTimestamp,
  compareSelfCheck,
  parseSelfCheckReport,
  sha256SumsLine,
  type ExpectedPayload,
} from './lib/package-lib.ts';

/** パッチツールの exe（patcher\build.ps1 の出力） */
const EXE_PATH = join('dist', 'publish', 'SentienceJP-Patcher.exe');
/** 現在のペイロードの manifest（npm run build の出力。exe に埋め込まれているはずのもの） */
const MANIFEST_PATH = join(PAYLOAD_DIR, 'manifest.json');
/** 同梱するテキストの置き場所 */
const RELEASE_DIR = 'release';
/**
 * 同梱するテキスト（UTF-8）。想定外のファイル（desktop.ini・Thumbs.db・作業中のメモなど）が
 * 混入しないよう、フォルダの中身をすべて入れるのではなく明示したものだけを入れる。
 */
const RELEASE_TEXTS: readonly string[] = ['はじめにお読みください.txt', 'LICENSE.txt', 'THIRD-PARTY-NOTICES.txt'];
/** 出力先 */
const OUT_DIR = join('dist', 'release');
/** exe の自己検査の時間切れ（patcher\build.ps1 と同じ 120 秒。単一 exe の初回起動は展開に時間がかかることがある） */
const SELF_CHECK_TIMEOUT_MS = 120_000;
/** 使い方（引数の誤りのときに表示する） */
const USAGE = 'npm run package（引数はありません）';

const sha256 = (data: Buffer): string => createHash('sha256').update(data).digest('hex');

/** 同梱テキストを読み込む（Windows のメモ帳で読みやすいよう、改行を CRLF に揃える） */
const readReleaseText = (folder: string, name: string, modified: Date): ZipEntry => {
  const path = join(RELEASE_DIR, name);
  if (!existsSync(path)) {
    throw new Error(`同梱ファイル ${path} がありません`);
  }
  const text = readFileSync(path, 'utf8').replace(/\r?\n/g, '\r\n');
  return { name: `${folder}/${name}`, data: Buffer.from(text, 'utf8'), modified };
};

/**
 * exe を自己検査モードで起動し、埋め込まれたペイロードと版が現在のものと一致することを確かめる（一致しなければ例外）。
 * 結果ファイルは一時フォルダに作り、終わったら削除する。exe の起動と結果ファイルの読み込みだけを行い、判定は package-lib.ts。
 */
const verifyExeMatchesPayload = (exePath: string, expected: ExpectedPayload): void => {
  const dir = mkdtempSync(join(tmpdir(), 'sentience-jp-selfcheck-'));
  try {
    const reportPath = join(dir, 'selfcheck.txt');
    const result = spawnSync(resolve(exePath), [VERIFY_PAYLOAD_SWITCH, reportPath], {
      stdio: 'ignore',
      timeout: SELF_CHECK_TIMEOUT_MS,
      windowsHide: true,
    });
    if (result.error !== undefined) {
      // 起動できない（Windows 以外・権限）、時間切れ（ETIMEDOUT。画面が出て止まっている可能性）など
      throw new Error(`${exePath} の自己検査を実行できませんでした（${result.error.message}）。${EXE_MISMATCH_GUIDANCE}`, { cause: result.error });
    }
    const report = existsSync(reportPath) ? decodeUtf8(readFileSync(reportPath), reportPath) : undefined;
    if (result.status !== 0) {
      const detail = report === undefined ? '結果ファイルが作成されませんでした' : parseSelfCheckReport(report).firstLine;
      throw new Error(`${exePath} の自己検査に失敗しました（終了コード ${result.status ?? result.signal ?? '不明'}: ${detail}）。${EXE_MISMATCH_GUIDANCE}`);
    }
    if (report === undefined) {
      throw new Error(`${exePath} の自己検査の結果ファイルが作成されませんでした。${EXE_MISMATCH_GUIDANCE}`);
    }
    const problems = compareSelfCheck(parseSelfCheckReport(report), expected);
    if (problems.length > 0) {
      throw new Error(`${EXE_MISMATCH_GUIDANCE}\n${problems.map((p) => `  - ${p}`).join('\n')}`);
    }
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
};

/** git の HEAD のコミット時刻（UNIX 時刻の秒の文字列）。シェルを介さずに git を起動する（取得できなければ例外） */
const readGitCommitTime = (): string =>
  execFileSync('git', ['log', '-1', '--format=%ct'], { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'], windowsHide: true }).trim();

const main = (): void => {
  // 引数は受け付けない（打ち間違えたオプションを黙って無視しないよう、あれば使い方を表示して終了する）
  parseCliArgs(process.argv.slice(2), { usage: USAGE, options: {}, allowPositionals: false });
  const version = asString(asRecord(readJson('package.json'), 'package.json')['version'], 'package.json.version');
  if (!existsSync(EXE_PATH)) {
    throw new Error(`${EXE_PATH} がありません。先に patcher\\build.ps1 を実行してください`);
  }
  if (!existsSync(MANIFEST_PATH)) {
    throw new Error(`${MANIFEST_PATH} がありません。npm run build と patcher\\build.ps1 を実行してください`);
  }
  // 古い exe（古い訳文入り・古い版）を新しい版番号の Zip に詰めないよう、Zip を作る前に照合する
  verifyExeMatchesPayload(EXE_PATH, { version, manifestSha256: sha256(readFileSync(MANIFEST_PATH)) });

  // 同じ入力から同じ Zip を作るため、更新日時に現在時刻を使わない（SOURCE_DATE_EPOCH → git のコミット時刻）
  const timestamp = chooseZipTimestamp(process.env['SOURCE_DATE_EPOCH'], readGitCommitTime, () => new Date());
  if (timestamp.warning !== undefined) {
    console.warn(`警告: ${timestamp.warning}`);
  }
  const folder = `SentienceJP-Patcher-v${version}`;
  const exe = readFileSync(EXE_PATH);
  const texts = RELEASE_TEXTS.map((name) => readReleaseText(folder, name, timestamp.date));
  const entries: ZipEntry[] = [{ name: `${folder}/SentienceJP-Patcher.exe`, data: exe, modified: timestamp.date }, ...texts];
  const zip = createZip(entries);
  mkdirSync(OUT_DIR, { recursive: true });
  const zipName = `${folder}.zip`;
  writeFileSync(join(OUT_DIR, zipName), zip);
  // SHA256SUMS.txt は配布する Zip だけを載せる（sha256sum -c でそのまま検証できる形式）。
  // exe のハッシュ値は展開後の確認用として画面にだけ表示する
  const zipSum = sha256SumsLine(sha256(zip), zipName);
  writeFileSync(join(OUT_DIR, 'SHA256SUMS.txt'), `${zipSum}\n`, 'utf8');
  console.log(`配布用 Zip を作成しました: ${join(OUT_DIR, zipName)}（${(zip.length / 1024 / 1024).toFixed(1)} MB）`);
  entries.forEach((e) => console.log(`  ${e.name}（${e.data.length.toLocaleString()} バイト）`));
  console.log(`更新日時: ${timestamp.date.toISOString()}（${timestamp.source}。Zip には UTC の日時で記録）`);
  console.log(`SHA-256（Zip）: ${zipSum}`);
  console.log(`SHA-256（展開後の exe）: ${sha256SumsLine(sha256(exe), 'SentienceJP-Patcher.exe')}`);
};

try {
  main();
} catch (error) {
  console.error(error instanceof Error ? error.message : String(error));
  process.exitCode = 1;
}
