import json
import subprocess
import sys
import unittest
from datetime import datetime, timedelta, timezone
from math import fsum
from pathlib import Path

from model import forecast


START = datetime(2025, 1, 1, 15, tzinfo=timezone.utc)


def fixture(match_id, kickoff, home="strong", away="weak"):
    return {
        "id": match_id,
        "competition": "synthetic-league",
        "kickoff": kickoff.isoformat(),
        "homeTeamId": home,
        "awayTeamId": away,
    }


def completed(match_id, kickoff, home="strong", away="weak", score=(3, 0)):
    return dict(
        fixture(match_id, kickoff, home, away), homeGoals=score[0], awayGoals=score[1]
    )


def balanced_venues_history():
    return [
        completed(str(day), START + timedelta(days=day), "weak", "strong", (0, 3))
        if day % 2
        else completed(str(day), START + timedelta(days=day))
        for day in range(120)
    ]


class ForecastTests(unittest.TestCase):
    def test_json_worker_publishes_consistent_probabilities(self):
        request = {
            "history": [completed("played", START)],
            "fixtures": [fixture("next", START + timedelta(days=1))],
        }
        worker = Path(__file__).with_name("model.py")
        result = subprocess.run(
            [sys.executable, str(worker)],
            input=json.dumps(request),
            text=True,
            capture_output=True,
            timeout=10,
            check=True,
        )
        response = json.loads(result.stdout)
        self.assertEqual(response["tuning"]["status"], "insufficient-history")
        prediction, = response["predictions"]
        self.assertEqual(prediction["id"], "next")

        scores = prediction["scoreDistribution"]
        omitted = prediction["scoreDistributionOmittedMass"]
        self.assertAlmostEqual(fsum(row["probability"] for row in scores) + omitted, 1)
        self.assertAlmostEqual(
            prediction["homeWin"] + prediction["draw"] + prediction["awayWin"], 1
        )
        buckets = {
            "homeWin": lambda home, away: home > away,
            "draw": lambda home, away: home == away,
            "awayWin": lambda home, away: home < away,
            "over25": lambda home, away: home + away >= 3,
            "bothTeamsScore": lambda home, away: home > 0 and away > 0,
        }
        for key, includes in buckets.items():
            with self.subTest(market=key):
                mass = fsum(
                    row["probability"] for row in scores
                    if includes(row["homeGoals"], row["awayGoals"])
                )
                self.assertAlmostEqual(prediction[key], mass, delta=omitted + 1e-12)
        self.assertAlmostEqual(prediction["over25"] + prediction["under25"], 1)
        self.assertAlmostEqual(prediction["bothTeamsScore"] + prediction["bothTeamsScoreNo"], 1)

    def test_uses_result_only_after_its_completion_time(self):
        finish = START + timedelta(hours=2)
        fixtures = [fixture("next", START + timedelta(days=1))]
        row = dict(completed("played", START), completedAt=finish.isoformat())
        as_of = finish.isoformat()
        before = forecast({"history": [row], "fixtures": fixtures, "asOf": as_of})
        without_result = forecast({"history": [], "fixtures": fixtures, "asOf": as_of})
        self.assertEqual(before, without_result)

        after = forecast({
            "history": [row],
            "fixtures": fixtures,
            "asOf": (finish + timedelta(seconds=1)).isoformat(),
        })
        self.assertEqual(after["tuning"]["historyMatches"], 1)
        self.assertGreater(after["predictions"][0]["homeWin"], before["predictions"][0]["homeWin"])

    def test_tuning_cannot_use_result_at_first_holdout_kickoff(self):
        history = balanced_venues_history()
        history[95]["completedAt"] = history[96]["kickoff"]
        original = forecast({"history": history, "fixtures": []})["tuning"]
        history[95]["homeGoals"], history[95]["awayGoals"] = 3, 0
        changed = forecast({"history": history, "fixtures": []})["tuning"]

        self.assertEqual(original["status"], "tuned")
        self.assertEqual(original["validationMatches"], 23)
        for key in (
            "validationLogLoss", "validationTotalGoalsLogLoss", "eloHalfLifeDays",
            "eloUpdate", "poissonWeight", "teamPriorMatches", "goalRateScale",
        ):
            with self.subTest(metric=key):
                self.assertEqual(original[key], changed[key])

    def test_model_beats_frequency_baseline_when_team_strength_persists(self):
        response = forecast({"history": balanced_venues_history(), "fixtures": []})
        tuning = response["tuning"]
        self.assertEqual(tuning["testMatches"], 24)
        self.assertEqual(tuning["correctPicks"], 24)
        self.assertLess(tuning["hybridLogLoss"], tuning["baselineLogLoss"])


if __name__ == "__main__":
    unittest.main()
