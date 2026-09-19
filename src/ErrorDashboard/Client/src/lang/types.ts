/**
 * A complete translation of `en.ts`.
 *
 * `en.ts` is declared `as const`, so every one of its values has a literal type. This mapped type
 * widens those back to `string` while keeping every area and every key *required* - which is the
 * whole point: a language file that forgets a key fails `tsc` instead of silently falling back to
 * English at runtime, where nobody would notice until a user did.
 *
 * Function entries (the plural forms) keep their exact parameter list, so a translation cannot drop
 * the count argument either.
 */
export type Translation<TSource> = {
  [Area in keyof TSource]: {
    [Key in keyof TSource[Area]]: TSource[Area][Key] extends (...args: infer TArgs) => string
      ? (...args: TArgs) => string
      : string;
  };
};
