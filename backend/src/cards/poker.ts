/**
 * Poker hands: the best five of any five to seven cards, as a score that
 * orders hands and a name a table can show. Hold'em settles its showdowns
 * with it, and its bots judge their chances with it.
 *
 * Cards are written as everywhere in the card hall: a rank (A 2-9 T J Q K)
 * then a suit (S H D C).
 */

export type Card = string;

const ORDER = "23456789TJQKA";

/** A card's rank, 2 to 14, the ace high. */
export const rankOf = (card: Card): number => ORDER.indexOf(card[0] as string) + 2;

export const CATEGORIES = [
	"HIGH CARD",
	"PAIR",
	"TWO PAIR",
	"THREE OF A KIND",
	"STRAIGHT",
	"FLUSH",
	"FULL HOUSE",
	"FOUR OF A KIND",
	"STRAIGHT FLUSH",
] as const;

export interface PokerHand {
	/** Higher beats lower; equal is a split. */
	readonly score: number;
	/** Its place in {@link CATEGORIES}. */
	readonly category: number;
	/** How a table calls it: "PAIR OF KINGS", "SEVENS FULL OF TWOS". */
	readonly name: string;
	/** The five cards that make it, the ones that count most first. */
	readonly five: Card[];
}

const ONE = [
	"",
	"ACE",
	"TWO",
	"THREE",
	"FOUR",
	"FIVE",
	"SIX",
	"SEVEN",
	"EIGHT",
	"NINE",
	"TEN",
	"JACK",
	"QUEEN",
	"KING",
	"ACE",
];
const one = (rank: number): string => ONE[rank] as string;
const many = (rank: number): string => (rank === 6 ? "SIXES" : `${one(rank)}S`);

/** The best five-card hand in the cards given. */
export function bestHand(cards: readonly Card[]): PokerHand {
	const byRank: Card[][] = Array.from({ length: 15 }, () => []);
	const bySuit = new Map<string, Card[]>();
	const sorted = [...cards].sort((a, b) => rankOf(b) - rankOf(a));
	for (const card of sorted) {
		(byRank[rankOf(card)] as Card[]).push(card);
		const suit = card[1] as string;
		const list = bySuit.get(suit);
		if (list === undefined) {
			bySuit.set(suit, [card]);
		} else {
			list.push(card);
		}
	}

	const flush = [...bySuit.values()].find((list) => list.length >= 5);
	if (flush !== undefined) {
		const run = straightIn(flush);
		if (run !== null) {
			const high = run.high;
			return made(
				8,
				run.cards,
				run.values,
				high === 14 ? "ROYAL FLUSH" : `STRAIGHT FLUSH, ${one(high)} HIGH`,
			);
		}
	}

	// Ranks held, the biggest groups first and the higher rank first within a size.
	const groups: number[] = [];
	for (let rank = 14; rank >= 2; rank -= 1) {
		if ((byRank[rank] as Card[]).length > 0) {
			groups.push(rank);
		}
	}
	groups.sort((a, b) => (byRank[b] as Card[]).length - (byRank[a] as Card[]).length || b - a);
	const first = groups[0] ?? 0;
	const second = groups[1] ?? 0;
	const size = (rank: number): number => (byRank[rank] as Card[]).length;
	const others = (...used: number[]): Card[] =>
		sorted.filter((card) => !used.includes(rankOf(card)));

	if (size(first) === 4) {
		const five = [...(byRank[first] as Card[]), ...others(first).slice(0, 1)];
		return made(7, five, five.map(rankOf), `FOUR ${many(first)}`);
	}

	if (size(first) === 3 && second > 0 && size(second) >= 2) {
		const five = [...(byRank[first] as Card[]), ...(byRank[second] as Card[]).slice(0, 2)];
		return made(6, five, five.map(rankOf), `${many(first)} FULL OF ${many(second)}`);
	}

	if (flush !== undefined) {
		const five = flush.slice(0, 5);
		return made(5, five, five.map(rankOf), `FLUSH, ${one(rankOf(five[0] as Card))} HIGH`);
	}

	const run = straightIn(sorted);
	if (run !== null) {
		return made(4, run.cards, run.values, `STRAIGHT, ${one(run.high)} HIGH`);
	}

	if (size(first) === 3) {
		const five = [...(byRank[first] as Card[]), ...others(first).slice(0, 2)];
		return made(3, five, five.map(rankOf), `THREE ${many(first)}`);
	}

	if (size(first) === 2 && second > 0 && size(second) === 2) {
		const five = [
			...(byRank[first] as Card[]),
			...(byRank[second] as Card[]),
			...others(first, second).slice(0, 1),
		];
		return made(2, five, five.map(rankOf), `${many(first)} AND ${many(second)}`);
	}

	if (size(first) === 2) {
		const five = [...(byRank[first] as Card[]), ...others(first).slice(0, 3)];
		return made(1, five, five.map(rankOf), `PAIR OF ${many(first)}`);
	}

	const five = sorted.slice(0, 5);
	return made(0, five, five.map(rankOf), `${one(first)} HIGH`);
}

/** The highest five in a row among the cards, the ace counting low as well as high. */
function straightIn(
	cards: readonly Card[],
): { cards: Card[]; values: number[]; high: number } | null {
	const byRank = new Map<number, Card>();
	for (const card of cards) {
		const rank = rankOf(card);
		if (!byRank.has(rank)) {
			byRank.set(rank, card);
		}
	}
	const ace = byRank.get(14);
	if (ace !== undefined) {
		byRank.set(1, ace);
	}

	for (let high = 14; high >= 5; high -= 1) {
		const values = [high, high - 1, high - 2, high - 3, high - 4];
		if (values.every((value) => byRank.has(value))) {
			return { cards: values.map((value) => byRank.get(value) as Card), values, high };
		}
	}
	return null;
}

function made(category: number, five: Card[], values: number[], name: string): PokerHand {
	let score = category;
	for (let index = 0; index < 5; index += 1) {
		score = score * 15 + (values[index] ?? 0);
	}
	return { score, category, name, five };
}
