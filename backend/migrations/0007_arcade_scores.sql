-- 0007_arcade_scores: the arcade's Grid-wide high-score boards.
--
-- One row per score on a board. A board is a game (or a game's track and
-- difficulty, or a day's challenge) and keeps only its best ten: every write
-- trims the board straight after, so the table stays a few hundred rows no
-- matter how much the arcade is played, and reading every board is cheap.
--
-- `id` is chosen by the deck when the score is signed, not by the database,
-- so a deck that retries a submission whose answer it never received cannot
-- put the same run on the board twice.
--
-- Nothing here names a player: `initials` is the six-character name typed at
-- the cabinet, and no character, account or address is stored.
CREATE TABLE arcade_scores (
	id TEXT PRIMARY KEY NOT NULL,
	board TEXT NOT NULL,
	initials TEXT NOT NULL,
	score INTEGER NOT NULL CHECK (score > 0),
	created_at TEXT NOT NULL
);

-- The ranking order. Covers the qualification check on every submission and
-- the trim after it, which both look at one board's scores best first.
CREATE INDEX arcade_scores_board_rank ON arcade_scores (board, score DESC, created_at ASC, id ASC);
