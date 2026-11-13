"""Predict 90-minute soccer markets from a JSON snapshot on standard input."""

from __future__ import annotations

import json
import sys
from bisect import bisect_left
from collections import defaultdict
from dataclasses import dataclass, field
from datetime import datetime, timedelta, timezone
from math import exp, fsum, log


MAX_GOALS = 24
MIN_PUBLISHED_SCORE_PROBABILITY = 1e-12
DEFAULT_ELO_UPDATE = 28
ELO_UPDATES = (DEFAULT_ELO_UPDATE, 56, 84, 112)
DEFAULT_ELO_HALF_LIFE_DAYS = 550
ELO_HALF_LIVES = (365, DEFAULT_ELO_HALF_LIFE_DAYS, 900, 1460)
DEFAULT_POISSON_WEIGHT = 0.6
DEFAULT_TEAM_PRIOR = 12
TEAM_PRIORS = (6, DEFAULT_TEAM_PRIOR, 24)
POISSON_WEIGHTS = (0.0, 0.2, 0.4, DEFAULT_POISSON_WEIGHT, 0.8, 1.0)
GOAL_RATE_SCALES = (0.8, 0.9, 1.0, 1.1, 1.2)
HOME_WIN, DRAW, AWAY_WIN = range(3)


def outcome(home_goals: int, away_goals: int) -> int:
    if home_goals > away_goals:
        return HOME_WIN
    if home_goals < away_goals:
        return AWAY_WIN
    return DRAW


def timestamp(value: str) -> datetime:
    if not isinstance(value, str):
        raise ValueError("Match times must be ISO 8601 strings with a UTC offset.")
    parsed = datetime.fromisoformat(value)
    if parsed.tzinfo is None:
        raise ValueError("Match times must include a UTC offset.")
    return parsed.astimezone(timezone.utc)


def identifier(value: object) -> str:
    if type(value) not in (str, int) or not str(value).strip():
        raise ValueError("Match, competition, and team IDs must be nonempty strings or integers.")
    return str(value)


@dataclass(frozen=True, slots=True)
class Fixture:
    id: str
    competition: str
    kickoff: datetime
    home_team: str
    away_team: str
    neutral: bool

    @classmethod
    def from_json(cls, row: dict) -> Fixture:
        fixture = cls(
            identifier(row["id"]),
            identifier(row["competition"]),
            timestamp(row["kickoff"]),
            identifier(row["homeTeamId"]),
            identifier(row["awayTeamId"]),
            row.get("neutralVenue", False),
        )
        if fixture.home_team == fixture.away_team or type(fixture.neutral) is not bool:
            raise ValueError("A match needs two different teams and an optional boolean neutralVenue.")
        return fixture


@dataclass(frozen=True, slots=True)
class CompletedMatch:
    fixture: Fixture
    home_goals: int
    away_goals: int
    available_at: datetime

    @classmethod
    def from_json(cls, row: dict) -> CompletedMatch:
        fixture = Fixture.from_json(row)
        home_goals = row["homeGoals"]
        away_goals = row["awayGoals"]
        if any(type(goals) is not int or goals < 0 for goals in (home_goals, away_goals)):
            raise ValueError("Finished scores must be nonnegative integers.")
        completed_at = row.get("completedAt")
        available_at = (
            timestamp(completed_at)
            if completed_at is not None
            else fixture.kickoff + timedelta(hours=3)
        )
        if available_at <= fixture.kickoff:
            raise ValueError("completedAt must be after kickoff.")
        return cls(fixture, home_goals, away_goals, available_at)


@dataclass(slots=True)
class CompetitionForm:
    matches: int = 0
    home_goals: int = 0
    away_goals: int = 0
    outcomes: list[int] = field(default_factory=lambda: [0, 0, 0])

    def observe(self, match: CompletedMatch) -> None:
        self.matches += 1
        self.home_goals += match.home_goals
        self.away_goals += match.away_goals
        self.outcomes[outcome(match.home_goals, match.away_goals)] += 1

    def goal_rates(self, neutral: bool) -> tuple[float, float]:
        prior_matches = 24
        if neutral:
            total_goals = self.home_goals + self.away_goals + prior_matches * 2.6
            neutral_rate = total_goals / (2 * (self.matches + prior_matches))
            return neutral_rate, neutral_rate
        return (
            (self.home_goals + prior_matches * 1.45) / (self.matches + prior_matches),
            (self.away_goals + prior_matches * 1.15) / (self.matches + prior_matches),
        )

    def baseline(self) -> tuple[float, float, float]:
        total = self.matches + 3
        home, draw, away = self.outcomes
        return (home + 1) / total, (draw + 1) / total, (away + 1) / total


@dataclass(slots=True)
class TeamForm:
    matches: float = 0.0
    goals_for: float = 0.0
    goals_against: float = 0.0
    updated: datetime | None = None

    def decay_at(self, at: datetime) -> float:
        if self.updated is None:
            return 1.0
        return 0.5 ** ((at - self.updated).total_seconds() / timedelta(days=210).total_seconds())

    def observe(self, goals_for: int, goals_against: int, at: datetime) -> None:
        decay = self.decay_at(at)
        self.matches = self.matches * decay + 1
        self.goals_for = self.goals_for * decay + goals_for
        self.goals_against = self.goals_against * decay + goals_against
        self.updated = at

    def rates(
        self, league_for: float, league_against: float, prior: int, at: datetime
    ) -> tuple[float, float]:
        decay = self.decay_at(at)
        weighted_matches = self.matches * decay + prior
        return (
            (self.goals_for * decay + league_for * prior) / weighted_matches,
            (self.goals_against * decay + league_against * prior) / weighted_matches,
        )


class ForecastState:
    def __init__(
        self, elo_half_life_days: int = DEFAULT_ELO_HALF_LIFE_DAYS, elo_update: int = DEFAULT_ELO_UPDATE
    ) -> None:
        self.leagues: dict[tuple[str, bool], CompetitionForm] = defaultdict(CompetitionForm)
        self.teams: dict[tuple[str, str, str], TeamForm] = defaultdict(TeamForm)
        self.ratings: dict[str, tuple[float, datetime]] = {}
        self.elo_half_life = timedelta(days=elo_half_life_days)
        self.elo_update = elo_update

    def league(self, match: Fixture) -> CompetitionForm:
        return self.leagues[match.competition, match.neutral]

    def team_forms(self, match: Fixture) -> tuple[TeamForm, TeamForm]:
        home_role = "neutral" if match.neutral else "home"
        away_role = "neutral" if match.neutral else "away"
        return (
            self.teams[match.competition, match.home_team, home_role],
            self.teams[match.competition, match.away_team, away_role],
        )

    def rating(self, team: str, at: datetime) -> float:
        rating, updated = self.ratings.get(team, (0.0, at))
        return rating * 0.5 ** ((at - updated) / self.elo_half_life)

    def elo(self, match: Fixture, at: datetime) -> tuple[float, float, float]:
        league = self.league(match)
        draw_rate = (league.outcomes[DRAW] + 30 * 0.26) / (league.matches + 30)
        home_advantage = 0 if match.neutral else 55
        advantage = self.rating(match.home_team, at) - self.rating(match.away_team, at)
        home = 10 ** ((advantage + home_advantage) / 800)
        away = 1 / home
        draw = 2 * draw_rate / (1 - draw_rate)
        total = home + draw + away
        return home / total, draw / total, away / total

    def goal_rates(
        self, match: Fixture, prior: int, at: datetime
    ) -> tuple[float, float]:
        league_home, league_away = self.league(match).goal_rates(match.neutral)
        home_form, away_form = self.team_forms(match)
        home_attack, home_defence = home_form.rates(league_home, league_away, prior, at)
        away_attack, away_defence = away_form.rates(league_away, league_home, prior, at)
        home_rate = home_attack * away_defence / league_home
        away_rate = away_attack * home_defence / league_away
        return home_rate, away_rate

    def observe(self, match: CompletedMatch) -> None:
        fixture = match.fixture
        at = match.available_at
        home, draw, _ = self.elo(fixture, at)
        actual = (1.0, 0.5, 0.0)[outcome(match.home_goals, match.away_goals)]
        change = self.elo_update * (actual - home - draw / 2)
        self.ratings[fixture.home_team] = (self.rating(fixture.home_team, at) + change, at)
        self.ratings[fixture.away_team] = (self.rating(fixture.away_team, at) - change, at)
        home_form, away_form = self.team_forms(fixture)
        home_form.observe(match.home_goals, match.away_goals, at)
        away_form.observe(match.away_goals, match.home_goals, at)
        self.league(fixture).observe(match)


class Timeline:
    """Expose an expanding snapshot of results available before a forecast."""

    def __init__(
        self, history: list[CompletedMatch],
        elo_half_life_days: int = DEFAULT_ELO_HALF_LIFE_DAYS, elo_update: int = DEFAULT_ELO_UPDATE,
    ) -> None:
        self.results = iter(sorted(history, key=lambda match: (match.available_at, match.fixture.id)))
        self.next_result = next(self.results, None)
        self.state = ForecastState(elo_half_life_days, elo_update)

    def before(self, at: datetime) -> ForecastState:
        while self.next_result is not None and self.next_result.available_at < at:
            self.state.observe(self.next_result)
            self.next_result = next(self.results, None)
        return self.state


def poisson_pmf(rate: float) -> list[float]:
    rate = max(0.15, min(6.0, rate))
    probabilities = [exp(-rate)]
    for goals in range(1, MAX_GOALS + 1):
        probabilities.append(probabilities[-1] * rate / goals)
    total = sum(probabilities)
    return [probability / total for probability in probabilities]


def poisson_outcomes(home: list[float], away: list[float]) -> tuple[float, float, float]:
    home_win, draw, away_win = 0.0, 0.0, 0.0
    home_below, away_below = 0.0, 0.0
    for home_probability, away_probability in zip(home, away):
        home_win += home_probability * away_below
        draw += home_probability * away_probability
        away_win += away_probability * home_below
        home_below += home_probability
        away_below += away_probability
    return home_win, draw, away_win


def poisson_scores(rates: tuple[float, float], scale: float = 1.0) -> tuple[list[float], list[float]]:
    home_rate, away_rate = rates
    return poisson_pmf(home_rate * scale), poisson_pmf(away_rate * scale)


def blend(poisson: float, elo: float, weight: float) -> float:
    return weight * poisson + (1 - weight) * elo


def score_distribution(
    rates: tuple[float, float],
    probabilities: tuple[float, float, float],
    scale: float,
) -> list[tuple[int, int, float]]:
    # Adjust each score by the change to its 1X2 bucket so every market comes
    # from one score distribution.
    home_scores, away_scores = poisson_scores(rates, scale)
    poisson = poisson_outcomes(home_scores, away_scores)
    adjustments = [combined / initial for combined, initial in zip(probabilities, poisson)]
    return [
        (home, away, home_probability * away_probability * adjustments[outcome(home, away)])
        for home, home_probability in enumerate(home_scores)
        for away, away_probability in enumerate(away_scores)
    ]


def goal_markets(scores: list[tuple[int, int, float]]) -> tuple[float, float, float, float]:
    over, btts, home_mean, away_mean = 0.0, 0.0, 0.0, 0.0
    for home, away, probability in scores:
        if home + away >= 3:
            over += probability
        if home > 0 and away > 0:
            btts += probability
        home_mean += home * probability
        away_mean += away * probability
    return over, btts, home_mean, away_mean


def total_goals_loss(scores: list[tuple[int, int, float]], actual_goals: int) -> float:
    probability = sum(p for home, away, p in scores if home + away == actual_goals)
    return -log(max(probability, 1e-12))


def tune(history: list[CompletedMatch]) -> dict:
    count = len(history)
    tuning = {
        "historyMatches": count,
        "trainingMatches": count,
        "validationMatches": 0,
        "testMatches": 0,
        "poissonWeight": DEFAULT_POISSON_WEIGHT,
        "eloHalfLifeDays": DEFAULT_ELO_HALF_LIFE_DAYS,
        "eloUpdate": DEFAULT_ELO_UPDATE,
        "teamPriorMatches": DEFAULT_TEAM_PRIOR,
        "goalRateScale": 1.0,
        "validationLogLoss": None,
        "validationTotalGoalsLogLoss": None,
        "hybridLogLoss": None,
        "poissonLogLoss": None,
        "eloLogLoss": None,
        "baselineLogLoss": None,
        "correctPicks": None,
        "baselineCorrectPicks": None,
        "firstTestKickoff": None,
        "lastTestKickoff": None,
        "over25LogLoss": None,
        "bothTeamsScoreLogLoss": None,
        "totalGoalsLogLoss": None,
        "predictedTotalGoals": None,
        "observedTotalGoals": None,
        "status": "insufficient-history",
    }
    if count < 100:
        return tuning

    kickoffs = [match.fixture.kickoff for match in history]
    validation_start = bisect_left(kickoffs, kickoffs[count * 3 // 5])
    test_start = bisect_left(kickoffs, kickoffs[count * 4 // 5])
    # A match can kick off before the holdout but finish after it starts.
    validation = [
        match for match in history[validation_start:test_start]
        if match.available_at < kickoffs[test_start]
    ]
    test = history[test_start:]
    if validation_start < 60 or len(validation) < 20 or len(test) < 20:
        return tuning

    elo_settings = [(days, update) for days in ELO_HALF_LIVES for update in ELO_UPDATES]
    timelines = {settings: Timeline(history, *settings) for settings in elo_settings}
    candidates = {
        (prior, weight, days, update): 0.0
        for prior in TEAM_PRIORS for weight in POISSON_WEIGHTS for days, update in elo_settings
    }
    for match in validation:
        fixture = match.fixture
        actual = outcome(match.home_goals, match.away_goals)
        elo_by_settings = {
            settings: timeline.before(fixture.kickoff).elo(fixture, fixture.kickoff)[actual]
            for settings, timeline in timelines.items()
        }
        # Elo settings do not change the separate Poisson team form.
        state = timelines[DEFAULT_ELO_HALF_LIFE_DAYS, DEFAULT_ELO_UPDATE].state
        for prior in TEAM_PRIORS:
            rates = state.goal_rates(fixture, prior, fixture.kickoff)
            poisson = poisson_outcomes(*poisson_scores(rates))[actual]
            for weight in POISSON_WEIGHTS:
                for (days, update), elo in elo_by_settings.items():
                    candidates[prior, weight, days, update] -= log(blend(poisson, elo, weight))

    prior, weight, days, update = min(
        candidates,
        key=lambda candidate: (
            candidates[candidate],
            abs(candidate[1] - DEFAULT_POISSON_WEIGHT),
            abs(candidate[0] - DEFAULT_TEAM_PRIOR),
            abs(candidate[2] - DEFAULT_ELO_HALF_LIFE_DAYS),
            abs(candidate[3] - DEFAULT_ELO_UPDATE),
        ),
    )

    # With the outcome blend fixed, tune the total-goal rate on validation alone.
    timeline = Timeline(history, days, update)
    goal_candidates = dict.fromkeys(GOAL_RATE_SCALES, 0.0)
    for match in validation:
        fixture = match.fixture
        state = timeline.before(fixture.kickoff)
        rates = state.goal_rates(fixture, prior, fixture.kickoff)
        poisson = poisson_outcomes(*poisson_scores(rates))
        elo = state.elo(fixture, fixture.kickoff)
        probabilities = tuple(blend(p, e, weight) for p, e in zip(poisson, elo))
        for scale in GOAL_RATE_SCALES:
            scores = score_distribution(rates, probabilities, scale)
            goal_candidates[scale] += total_goals_loss(scores, match.home_goals + match.away_goals)
    goal_scale = min(goal_candidates, key=lambda scale: (goal_candidates[scale], abs(scale - 1)))

    hybrid_loss, poisson_loss, elo_loss, baseline_loss = 0.0, 0.0, 0.0, 0.0
    correct_picks, baseline_correct_picks = 0, 0
    over_loss, btts_loss, exact_total_loss = 0.0, 0.0, 0.0
    predicted_goals, observed_goals = 0.0, 0
    for match in test:
        fixture = match.fixture
        actual = outcome(match.home_goals, match.away_goals)
        state = timeline.before(fixture.kickoff)
        rates = state.goal_rates(fixture, prior, fixture.kickoff)
        poisson = poisson_outcomes(*poisson_scores(rates))
        elo = state.elo(fixture, fixture.kickoff)
        baseline = state.league(fixture).baseline()
        probabilities = tuple(blend(p, e, weight) for p, e in zip(poisson, elo))
        hybrid_loss -= log(probabilities[actual])
        poisson_loss -= log(poisson[actual])
        elo_loss -= log(elo[actual])
        baseline_loss -= log(baseline[actual])
        correct_picks += probabilities.index(max(probabilities)) == actual
        baseline_correct_picks += baseline.index(max(baseline)) == actual

        scores = score_distribution(rates, probabilities, goal_scale)
        over, btts, home_mean, away_mean = goal_markets(scores)
        total_goals = match.home_goals + match.away_goals
        over_loss -= log(over if total_goals >= 3 else 1 - over)
        btts_loss -= log(btts if match.home_goals > 0 and match.away_goals > 0 else 1 - btts)
        exact_total_loss += total_goals_loss(scores, total_goals)
        predicted_goals += home_mean + away_mean
        observed_goals += total_goals

    return {
        **tuning,
        "trainingMatches": validation_start,
        "validationMatches": len(validation),
        "testMatches": len(test),
        "poissonWeight": weight,
        "eloHalfLifeDays": days,
        "eloUpdate": update,
        "teamPriorMatches": prior,
        "goalRateScale": goal_scale,
        "validationLogLoss": candidates[prior, weight, days, update] / len(validation),
        "validationTotalGoalsLogLoss": goal_candidates[goal_scale] / len(validation),
        "hybridLogLoss": hybrid_loss / len(test),
        "poissonLogLoss": poisson_loss / len(test),
        "eloLogLoss": elo_loss / len(test),
        "baselineLogLoss": baseline_loss / len(test),
        "correctPicks": correct_picks,
        "baselineCorrectPicks": baseline_correct_picks,
        "firstTestKickoff": test[0].fixture.kickoff.isoformat(),
        "lastTestKickoff": test[-1].fixture.kickoff.isoformat(),
        "over25LogLoss": over_loss / len(test),
        "bothTeamsScoreLogLoss": btts_loss / len(test),
        "totalGoalsLogLoss": exact_total_loss / len(test),
        "predictedTotalGoals": predicted_goals / len(test),
        "observedTotalGoals": observed_goals / len(test),
        "status": "tuned",
    }


def predict(fixture: Fixture, state: ForecastState, tuning: dict, origin: datetime) -> dict:
    rates = state.goal_rates(fixture, tuning["teamPriorMatches"], origin)
    poisson = poisson_outcomes(*poisson_scores(rates))
    elo = state.elo(fixture, origin)
    probabilities = tuple(
        blend(poisson_p, elo_p, tuning["poissonWeight"])
        for poisson_p, elo_p in zip(poisson, elo)
    )

    scores = score_distribution(rates, probabilities, tuning["goalRateScale"])
    over, btts, home_mean, away_mean = goal_markets(scores)
    top_scores = sorted(scores, key=lambda score: score[2], reverse=True)[:3]
    # Preserve probabilities from the normalized 0–24 goal grid. Dropping tiny
    # cells from the response reduces payload size while bounding any joint
    # estimate's omitted probability by the total reported below.
    published_scores = []
    omitted_probabilities = []
    for home, away, probability in scores:
        if probability >= MIN_PUBLISHED_SCORE_PROBABILITY:
            published_scores.append({"homeGoals": home, "awayGoals": away, "probability": probability})
        else:
            omitted_probabilities.append(probability)

    return {
        "id": fixture.id,
        "homeWin": probabilities[HOME_WIN],
        "draw": probabilities[DRAW],
        "awayWin": probabilities[AWAY_WIN],
        "over25": over,
        "under25": 1 - over,
        "bothTeamsScore": btts,
        "bothTeamsScoreNo": 1 - btts,
        "expectedHomeGoals": home_mean,
        "expectedAwayGoals": away_mean,
        "topScorelines": [
            {"homeGoals": home, "awayGoals": away, "probability": probability}
            for home, away, probability in top_scores
        ],
        "scoreDistribution": published_scores,
        "scoreDistributionOmittedMass": fsum(omitted_probabilities),
    }


def forecast(request: dict) -> dict:
    history_by_id = {}
    for row in request["history"]:
        match = CompletedMatch.from_json(row)
        previous = history_by_id.setdefault(match.fixture.id, match)
        if match != previous:
            raise ValueError(f"Conflicting results for historical match {match.fixture.id}.")
    history = sorted(
        history_by_id.values(), key=lambda match: (match.fixture.kickoff, match.fixture.id)
    )
    fixtures = [Fixture.from_json(row) for row in request["fixtures"]]
    if len({fixture.id for fixture in fixtures}) != len(fixtures):
        raise ValueError("Fixture IDs must be unique.")
    as_of = timestamp(request["asOf"]) if request.get("asOf") is not None else None
    origins = [
        min(as_of, fixture.kickoff) if as_of is not None else fixture.kickoff
        for fixture in fixtures
    ]
    tuning_origin = min(origins, default=as_of)
    available_history = [
        match for match in history if tuning_origin is None or match.available_at < tuning_origin
    ]
    tuning = tune(available_history)
    timeline = Timeline(history, tuning["eloHalfLifeDays"], tuning["eloUpdate"])
    predictions = {}
    for fixture, origin in sorted(zip(fixtures, origins), key=lambda row: row[1]):
        predictions[fixture.id] = predict(fixture, timeline.before(origin), tuning, origin)
    return {"predictions": [predictions[fixture.id] for fixture in fixtures], "tuning": tuning}


if __name__ == "__main__":
    try:
        json.dump(forecast(json.load(sys.stdin)), sys.stdout, allow_nan=False)
        print()
    except (KeyError, TypeError, ValueError) as error:
        print(f"Invalid forecast input: {error}", file=sys.stderr)
        sys.exit(1)
