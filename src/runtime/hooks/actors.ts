/**
 * アクター名・二つ名・プロフィールの日本語化。
 *
 * ツクールMVはニューゲーム時にアクター名をセーブデータへ複製するため、データ側は書き換えず、
 * 名前を取り出す時点（Game_Actor.prototype.name など）で翻訳する。
 *
 * ただし、次の 2 か所ではゲームの判定に名前そのものが使われるため、翻訳を一時的に止める。
 * - 条件分岐「アクターの名前が〜」（Game_Interpreter.prototype.command111）:
 *   イベントは英語の名前と比較するので、訳名を返すと判定が変わってしまう。
 * - 名前入力の処理（Window_NameEdit）: 初期値に訳名が入ると、そのまま決定したときに
 *   日本語の名前がセーブデータへ書き込まれてしまう。
 *
 * また、名前（name）はデータベースの既定の名前のままのときだけ訳す。名前入力でプレイヤーが付けた名前が
 * たまたま別のキャラクターの英語名と同じだった場合に、訳名へ置き換わって表示されないようにするため（RA-03）。
 */
import { getPrototype, isRecord, reportError, wrapMethod, type Method } from '../env.ts';
import { translatePlain } from '../translator.ts';

/** 翻訳を止めている処理の入れ子の深さ（0 のときだけ翻訳する） */
let rawNameDepth = 0;

/** 処理の実行中だけ、アクター名の翻訳を止める */
const withRawNames =
  (original: Method): Method =>
  function (this: unknown, ...args: unknown[]): unknown {
    rawNameDepth += 1;
    try {
      return original.apply(this, args);
    } finally {
      rawNameDepth -= 1;
    }
  };

/**
 * 現在の名前を訳してよいか（データベースの既定の名前のままか）を判定する。
 *
 * - 既定の名前と同じ: 訳す（通常の場合）
 * - 既定の名前と違う: 訳さない（名前入力でプレイヤーが付けた名前。本作は「名前の変更」コマンドを使わないため、
 *   既定と違う名前はプレイヤーの入力に限られる）
 * - データベースを読めない（通常は起こらない）: 従来どおり訳す（異常時に表示を英語へ切り替えないため）
 *
 * @param actor name() を呼ばれたアクター（他プラグインから別の文脈で呼ばれる可能性もあるので unknown で受ける）
 * @param value 現在の名前
 */
const isDefaultActorName = (actor: unknown, value: string): boolean => {
  const actorId = isRecord(actor) ? actor['_actorId'] : undefined;
  const database: unknown = (window as unknown as Record<string, unknown>)['$dataActors'];
  if (typeof actorId !== 'number' || !Array.isArray(database)) {
    return true;
  }
  const data: unknown = database[actorId];
  if (!isRecord(data) || typeof data['name'] !== 'string') {
    return true;
  }
  return data['name'] === value;
};

/**
 * 戻り値の文字列を翻訳するラッパーを作る。
 *
 * @param shouldTranslate 戻り値を訳してよいかの追加の条件（名前は既定の名前のときだけ訳す）
 */
const translateResult =
  (shouldTranslate: (self: unknown, value: string) => boolean) =>
  (original: Method): Method =>
    function (this: unknown, ...args: unknown[]): unknown {
      const value = original.apply(this, args);
      if (typeof value !== 'string' || value === '' || rawNameDepth > 0) {
        return value;
      }
      try {
        return shouldTranslate(this, value) ? translatePlain('name', value) : value;
      } catch (error) {
        reportError('アクター名の翻訳', error);
        return value;
      }
    };

/** 二つ名・プロフィールは常に訳す（プレイヤーが変更する手段が無いため） */
const always = (): boolean => true;

/** フックを差し込む */
export const installActorHooks = (): void => {
  const proto = Game_Actor.prototype as unknown as Record<string, unknown>;
  wrapMethod(proto, 'name', translateResult(isDefaultActorName), 'Game_Actor.prototype');
  ['nickname', 'profile'].forEach((method) => wrapMethod(proto, method, translateResult(always), 'Game_Actor.prototype'));
  wrapMethod(Game_Interpreter.prototype as unknown as Record<string, unknown>, 'command111', withRawNames, 'Game_Interpreter.prototype');
  wrapMethod(getPrototype('Window_NameEdit'), 'initialize', withRawNames, 'Window_NameEdit.prototype');
};
