// The timer's volume as a perceptual level (UX15). The slider holds a level from 0 to 100, and the gain falls
// 40 dB from the top of the range to the bottom, so equal steps sound like equal changes; with a linear gain
// the whole audible change would sit in the bottom fifth of the slider. 0 is silence. The level is what the
// browser keeps; the gain that the alarm, the generated music and the track multiply by is derived from it.

export const MIN_LEVEL = 0;
export const MAX_LEVEL = 100;
export const RANGE_DB = 40;
/** As loud as the 0.5 gain that was the default before levels. */
export const DEFAULT_LEVEL = 85;
/** Seconds for a gain change to settle about two thirds of the way, short enough to feel live without clicks. */
export const GLIDE_S = 0.015;

/** Any number to a whole level in range; anything else to the default. */
export function clampLevel(value) {
  if (typeof value !== 'number' || !Number.isFinite(value)) {
    return DEFAULT_LEVEL;
  }
  return Math.min(MAX_LEVEL, Math.max(MIN_LEVEL, Math.round(value)));
}

/** The linear gain for a level: 100 → 1, 75 → 0.316, 50 → 0.1, 0 → 0. */
export function gain(level) {
  return level === 0 ? 0 : 10 ** ((level / MAX_LEVEL - 1) * RANGE_DB / 20);
}

/**
 * The gain written to `rs.timer.volume` for a tab still on a bundle from before levels. At 0 it is a tiny
 * positive gain, because that bundle reads 0 as unset and would play at its 0.5 default.
 */
export const LEGACY_OFF_GAIN = 0.0001;

export function legacyGain(level) {
  return level === 0 ? LEGACY_OFF_GAIN : gain(level);
}

/** The level that sounds as loud as a gain stored before levels (`rs.timer.volume`); none or ≤ 0 is the default. */
export function levelFromLegacyGain(value) {
  if (!(value > 0) || !Number.isFinite(value)) {
    return DEFAULT_LEVEL;
  }
  return clampLevel(MAX_LEVEL * (1 + 20 * Math.log10(value) / RANGE_DB));
}

/** The level from the stored strings: the level when it is a number, else migrated from the legacy gain. */
export function storedLevel(level, legacyGain) {
  if (level != null && level.trim() !== '' && Number.isFinite(Number(level))) {
    return clampLevel(Number(level));
  }
  return levelFromLegacyGain(legacyGain == null || legacyGain.trim() === '' ? NaN : Number(legacyGain));
}

/** What the slider says: "60%", or "Off" at 0. */
export function levelText(level) {
  return level === 0 ? 'Off' : `${level}%`;
}

/** Moves an AudioParam to `target` smoothly; assigning `value` mid-sound would step it and click. */
export function glide(param, target, context) {
  param.setTargetAtTime(target, context.currentTime, GLIDE_S);
}
