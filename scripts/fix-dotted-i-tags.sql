-- Merges tag rows left behind by the pre-fix TagNormalizer, which could emit an ASCII
-- capital 'I' from Turkish 'İ'. Safe to re-run; a no-op once there is nothing to fix.
--
-- Assumes the only uppercase residue is ASCII, which holds because TagNormalizer already
-- folds everything else to ASCII before storing.
BEGIN;

-- No lowercase twin: rename in place. Fires Tags_au, which recomputes Cartoons.TagsText
-- for every cartoon carrying the tag, which in turn re-syncs the FTS row.
UPDATE Tags
SET Name = lower(Name)
WHERE Name <> lower(Name)
  AND lower(Name) NOT IN (SELECT Name FROM Tags);

-- A twin already exists: repoint the joins, then drop the stale row.
CREATE TEMP TABLE _merge AS
SELECT bad.Id AS bad_id, good.Id AS good_id
FROM Tags bad
JOIN Tags good ON good.Name = lower(bad.Name)
WHERE bad.Name <> lower(bad.Name);

CREATE TEMP TABLE _moves AS
SELECT ct.CartoonId AS CartoonId, m.good_id AS good_id
FROM CartoonTags ct
JOIN _merge m ON m.bad_id = ct.TagId;

-- Delete-then-insert rather than UPDATE: CartoonTags has AFTER INSERT and AFTER DELETE
-- triggers but no AFTER UPDATE, so an in-place TagId update would leave TagsText - and
-- therefore the FTS index - stale.
DELETE FROM CartoonTags WHERE TagId IN (SELECT bad_id FROM _merge);

-- OR IGNORE covers the cartoon that already carried both spellings.
INSERT OR IGNORE INTO CartoonTags (CartoonId, TagId)
SELECT CartoonId, good_id FROM _moves;

DELETE FROM Tags WHERE Id IN (SELECT bad_id FROM _merge);

DROP TABLE _moves;
DROP TABLE _merge;

COMMIT;
