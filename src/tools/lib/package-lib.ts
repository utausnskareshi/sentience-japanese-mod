/**
 * 配布用 Zip の作成（package.ts）の判定部分（純粋関数）。package.ts（CLI）から使い、単体テストで規則を確かめる。
 *
 * - exe の自己検査（--verify-payload）の結果の解析と、現在のペイロード・版との照合
 *   （古い exe（古い訳文入り）を新しい版番号の Zip に詰めないため）
 * - Zip の各ファイルの更新日時（同じ入力から同じ Zip を作るため、現在時刻を使わない）
 * - SHA256SUMS.txt の行（sha256sum -c でそのまま検証できる書式）
 */

/** パッチツールの exe の自己検査モードのスイッチ（patcher の PatcherCommandLine.VerifyPayloadSwitch と同じ） */
export const VERIFY_PAYLOAD_SWITCH = '--verify-payload';

/** exe がペイロード・版と一致しないときの案内 */
export const EXE_MISMATCH_GUIDANCE = 'exe が現在のペイロード／版と一致しません。patcher\\build.ps1 で作り直してください';

/** 自己検査の結果ファイルから読み取った値 */
export interface SelfCheckReport {
  /** 1 行目が "OK:" で始まるか（失敗時は "NG: …"） */
  readonly ok: boolean;
  /** 1 行目（表示用） */
  readonly firstLine: string;
  /** "patchVersion=<版> translationVersion=<版> files=<数>" の行のパッチの版（無ければ undefined） */
  readonly patchVersion: string | undefined;
  /** "manifestSha256=<小文字16進64桁>" の行の値（無ければ undefined。この行を出さない古い exe もある） */
  readonly manifestSha256: string | undefined;
}

/** BOM（U+FEFF） */
const BOM = String.fromCharCode(0xfeff);

/** 版の行（patcher の PayloadSelfCheck が 2 行目に出す形式） */
const VERSION_LINE = /^patchVersion=(\S+) translationVersion=\S+ files=\d+$/;

/** manifest.json の SHA-256 の行（patcher の PayloadSelfCheck が版の行の直後に出す形式） */
const MANIFEST_LINE = /^manifestSha256=([0-9a-f]{64})$/;

/**
 * 自己検査の結果ファイルの内容を解析する（純粋関数）。
 * 結果ファイルは UTF-8 で、BOM の有無と改行（CRLF / LF）は問わない。行は先頭から順に探し、最初に一致したものを使う。
 */
export const parseSelfCheckReport = (text: string): SelfCheckReport => {
  const lines = (text.startsWith(BOM) ? text.slice(1) : text).split(/\r?\n/).map((line) => line.trimEnd());
  const firstLine = lines[0] ?? '';
  const versionMatch = lines.map((line) => VERSION_LINE.exec(line)).find((m) => m !== null);
  const manifestMatch = lines.map((line) => MANIFEST_LINE.exec(line)).find((m) => m !== null);
  return {
    ok: firstLine.startsWith('OK:'),
    firstLine,
    patchVersion: versionMatch?.[1],
    manifestSha256: manifestMatch?.[1],
  };
};

/** 照合の基準（現在のペイロードと版） */
export interface ExpectedPayload {
  /** package.json の version */
  readonly version: string;
  /** dist/payload/manifest.json のバイト列の SHA-256（小文字 16 進） */
  readonly manifestSha256: string;
}

/**
 * 自己検査の結果が、現在のペイロード・版と一致するかを確かめる（純粋関数）。食い違いの説明を列挙する（一致なら空配列）。
 * manifest.json には各ファイルの SHA-256 が入り、自己検査は埋め込んだファイルと manifest の一致を確かめるため、
 * manifest.json のハッシュが一致すれば、exe の埋め込みペイロード全体が現在の dist/payload と一致する。
 */
export const compareSelfCheck = (report: SelfCheckReport, expected: ExpectedPayload): string[] => {
  const problems: string[] = [];
  if (!report.ok) {
    problems.push(`自己検査の結果が OK ではありません: ${report.firstLine}`);
  }
  if (report.patchVersion === undefined) {
    problems.push('自己検査の結果に patchVersion の行がありません');
  } else if (report.patchVersion !== expected.version) {
    problems.push(`exe のパッチの版 ${report.patchVersion} が package.json の版 ${expected.version} と異なります`);
  }
  if (report.manifestSha256 === undefined) {
    problems.push('自己検査の結果に manifestSha256 の行がありません（埋め込んだ manifest.json のハッシュを出力しない古い exe です）');
  } else if (report.manifestSha256 !== expected.manifestSha256) {
    problems.push(
      `exe に埋め込まれた manifest.json（SHA-256 ${report.manifestSha256}）が dist/payload/manifest.json（${expected.manifestSha256}）と異なります`,
    );
  }
  return problems;
};

/** Zip の更新日時の決め方の結果 */
export interface ZipTimestamp {
  readonly date: Date;
  /** どこから決めたか（表示用） */
  readonly source: 'SOURCE_DATE_EPOCH' | 'git のコミット時刻' | '現在時刻';
  /** 再現できない日時を使う場合の警告 */
  readonly warning: string | undefined;
}

/** DOS 日時（Zip の更新日時）で表せる範囲の UNIX 時刻（秒）。1980-01-01T00:00:00Z 〜 2107-12-31T23:59:58Z */
const DOS_EPOCH_RANGE = { min: Date.UTC(1980, 0, 1) / 1000, max: Date.UTC(2107, 11, 31, 23, 59, 58) / 1000 } as const;

/**
 * UNIX 時刻（整数の秒）の文字列を Date にする（純粋関数）。整数でない・Zip に記録できる範囲の外なら例外を投げる。
 *
 * @param name エラーメッセージに含める値の名前
 */
export const parseEpochSeconds = (raw: string, name: string): Date => {
  const text = raw.trim();
  if (!/^[0-9]+$/.test(text)) {
    throw new Error(`${name} は 0 以上の整数（UNIX 時刻の秒）で指定してください（値: ${JSON.stringify(raw)}）`);
  }
  const seconds = Number(text);
  if (!Number.isSafeInteger(seconds) || seconds < DOS_EPOCH_RANGE.min || seconds > DOS_EPOCH_RANGE.max) {
    throw new Error(`${name} が Zip に記録できる範囲（1980〜2107 年）の外です（値: ${text}）`);
  }
  return new Date(seconds * 1000);
};

/**
 * 配布 Zip の各ファイルの更新日時を決める（同じ入力から同じ Zip を作り、公開した SHA256SUMS と再ビルドを突き合わせられるように）。
 * 1. 環境変数 SOURCE_DATE_EPOCH（再現可能なビルドの慣習。指定されていて不正なら例外）
 * 2. git の HEAD のコミット時刻（readGitCommitTime。git log -1 --format=%ct の出力）
 * 3. どちらも得られなければ現在時刻（Zip が再現できない旨の警告つき）
 *
 * @param sourceDateEpoch 環境変数 SOURCE_DATE_EPOCH の値（未設定なら undefined）
 * @param readGitCommitTime コミット時刻（UNIX 時刻の秒の文字列）を返す。取得できなければ例外を投げる
 * @param now 現在時刻を返す
 */
export const chooseZipTimestamp = (sourceDateEpoch: string | undefined, readGitCommitTime: () => string, now: () => Date): ZipTimestamp => {
  if (sourceDateEpoch !== undefined) {
    return { date: parseEpochSeconds(sourceDateEpoch, 'SOURCE_DATE_EPOCH'), source: 'SOURCE_DATE_EPOCH', warning: undefined };
  }
  try {
    return { date: parseEpochSeconds(readGitCommitTime(), 'git のコミット時刻'), source: 'git のコミット時刻', warning: undefined };
  } catch (error) {
    const reason = error instanceof Error ? (error.message.split(/\r?\n/)[0] ?? error.message) : String(error);
    return {
      date: now(),
      source: '現在時刻',
      warning: `git のコミット時刻を取得できなかったため、現在時刻を使います（同じ入力からでも同じ Zip になりません）: ${reason}`,
    };
  }
};

/**
 * SHA256SUMS.txt の 1 行（sha256sum の書式: ハッシュ・空白 2 つ・ファイル名。sha256sum -c でそのまま検証できる）
 */
export const sha256SumsLine = (sha256: string, fileName: string): string => `${sha256}  ${fileName}`;
