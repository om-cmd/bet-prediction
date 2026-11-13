"use strict";

const competitionPicker = document.querySelector("#performance-competition");
const diagnostics = document.querySelector("#model-details");
const historyStamp = document.querySelector("#performance-freshness");
const notices = document.querySelector("#performance-notices");
const decimal = new Intl.NumberFormat("en-AU", { minimumFractionDigits: 2, maximumFractionDigits: 2 });
const logLoss = new Intl.NumberFormat("en-AU", { minimumFractionDigits: 3, maximumFractionDigits: 3 });
const percent = new Intl.NumberFormat("en-AU", { style: "percent", maximumFractionDigits: 1 });
const count = new Intl.NumberFormat("en-AU");
const date = new Intl.DateTimeFormat("en-AU", { dateStyle: "medium", timeStyle: "short" });
const day = new Intl.DateTimeFormat("en-AU", { dateStyle: "medium" });
let request = 0;

function element(tag, text, className) {
    const node = document.createElement(tag);
    if (text) node.textContent = text;
    if (className) node.className = className;
    return node;
}

function setLinks(competition) {
    const params = new URLSearchParams({ competition });
    document.querySelector("#performance-back").href = `/?${params}`;
    document.querySelector("#performance-home").href = `/?${params}`;
}

function renderCompetitions(competitions, selected) {
    const options = new Map();
    for (const item of competitions ?? []) {
        const group = item.group || "Competitions";
        if (!options.has(group)) {
            const section = element("optgroup");
            section.label = group;
            options.set(group, section);
        }
        const name = item.country && item.country !== "World" ? `${item.name} · ${item.country}` : item.name;
        const option = element("option", name);
        option.value = item.id;
        options.get(group).append(option);
    }
    competitionPicker.replaceChildren(...options.values());
    competitionPicker.value = selected;
    competitionPicker.disabled = !options.size;
}

function renderNotices(warnings, sourceUrl) {
    notices.replaceChildren(...(warnings ?? []).map((warning) => element("p", warning, "notice warning")));
    try {
        const source = new URL(sourceUrl);
        if (source.protocol !== "https:") return;
        const link = element("a", "Results history source");
        link.href = source.href;
        link.target = "_blank";
        link.rel = "noopener noreferrer";
        const note = element("p", null, "notice");
        note.append(link);
        notices.append(note);
    } catch {
        // An absent source URL leaves the diagnostics and history timestamp available.
    }
}

function renderModel(model, competition, forecastAsOf, latestResultKickoff) {
    const timestamps = [
        ["Forecast as of", forecastAsOf],
        ["Latest result used · kickoff", latestResultKickoff],
    ].flatMap(([label, value]) => {
        const timestamp = value ? new Date(value) : null;
        return timestamp && Number.isFinite(timestamp.valueOf())
            ? [element("span", `${label} ${date.format(timestamp)}`)]
            : [];
    });
    historyStamp.replaceChildren(...(timestamps.length ? timestamps : ["Results history timestamp unavailable"]));
    document.querySelector("#performance-diagnostics").textContent = competition?.name || "Forecast validation";
    if (!model) {
        diagnostics.replaceChildren(element("p", "Forecast validation is unavailable for this competition."));
        return;
    }

    const metrics = element("dl", null, "model-metrics");
    const accuracy = (correct) => model.testMatches > 0 && Number.isFinite(correct) ? correct / model.testMatches : null;
    const fields = [
        ["Initial history matches", model.trainingMatches, count],
        ["Tuning matches", model.validationMatches, count],
        ["Test matches", model.testMatches, count],
        ["1X2 top-pick accuracy", accuracy(model.correctPicks), percent],
        ["1X2 baseline accuracy", accuracy(model.baselineCorrectPicks), percent],
        ["1X2 log loss", model.logLoss, logLoss],
        ["1X2 baseline loss", model.baselineLogLoss, logLoss],
        ["1X2 Poisson loss", model.poissonLogLoss, logLoss],
        ["1X2 Elo loss", model.eloLogLoss, logLoss],
        ["1X2 Poisson weight", model.blendWeight, percent],
        ["Elo half-life (days)", model.eloHalfLifeDays, count],
        ["Elo update factor", model.eloUpdate, count],
        ["Total goals loss", model.totalGoalsLogLoss, logLoss],
        ["Over 2.5 loss", model.over25LogLoss, logLoss],
        ["Both teams score loss", model.bothTeamsScoreLogLoss, logLoss],
        ["Projected mean goals", model.predictedTotalGoals, decimal],
        ["Observed mean goals", model.observedTotalGoals, decimal],
        ["Goal rate scale", model.goalScale, decimal],
    ];
    for (const [label, value, format] of fields) {
        if (!Number.isFinite(value)) continue;
        const item = element("div");
        item.append(element("dt", label), element("dd", format.format(value)));
        metrics.append(item);
    }

    diagnostics.replaceChildren(element("p", `${model.name || "Hybrid model"} · ${model.tuned ? "Tuned blend" : "Default blend"}`), metrics);
    diagnostics.append(element("p", "Forecast inputs are confirmed 90-minute scores and fixture details. Player availability and starting lineups require a separate feed."));
    if (Number.isFinite(model.logLoss)) {
        if (model.firstTestKickoff && model.lastTestKickoff) {
            const testDates = day.formatRange(new Date(model.firstTestKickoff), new Date(model.lastTestKickoff));
            diagnostics.append(element("p", `Historical replay · ${testDates}`));
        }
        diagnostics.append(element("p", "Top-pick accuracy counts how often the highest-probability result occurred. The baseline uses the competition’s earlier home-win, draw and away-win frequencies."));
        diagnostics.append(element("p", "The replay uses results available before each kickoff. When a final-score timestamp is missing, it assumes the score was known three hours after kickoff."));
        diagnostics.append(element("p", "Log loss scores probability accuracy on held-out matches and penalises confident errors. Lower is better within the same market."));
        if (Number.isFinite(model.eloHalfLifeDays)) {
            diagnostics.append(element("p", "The Elo half-life controls how quickly past results lose influence. The update factor controls how strongly new results change a team’s rating."));
        }
    }
    diagnostics.append(element("p", "Goal markets use the Poisson score distribution adjusted to match the blended home-win, draw and away-win probabilities."));
    if (model.tuned && Number.isFinite(model.predictedTotalGoals) && Number.isFinite(model.observedTotalGoals)) {
        diagnostics.append(element("p", "The goal rate scale was selected on tuning matches. The mean goals shown above compare forecasts with observed goals on held-out matches."));
    }
}

async function loadPerformance(competition) {
    const current = ++request;
    setLinks(competition);
    diagnostics.setAttribute("aria-busy", "true");
    diagnostics.replaceChildren(element("p", "Loading diagnostics…"));
    historyStamp.textContent = "Loading results history…";
    notices.replaceChildren();
    try {
        const params = new URLSearchParams({ competition });
        const response = await fetch(`/api/model-performance?${params}`, { cache: "no-store", signal: AbortSignal.timeout(45000) });
        if (!response.ok) throw new Error(`Model performance returned HTTP ${response.status}.`);
        const data = await response.json();
        if (current !== request) return;
        const selected = data.competition?.id || competition;
        renderCompetitions(data.competitions, selected);
        setLinks(selected);
        renderNotices(data.warnings, data.sourceUrl);
        renderModel(data.model, data.competition, data.forecastAsOf, data.latestForecastResultKickoff);
    } catch {
        if (current !== request) return;
        historyStamp.textContent = "Results history timestamp unavailable";
        const retry = element("button", "Retry", "market-browser-action");
        retry.type = "button";
        retry.addEventListener("click", () => loadPerformance(competition));
        diagnostics.replaceChildren(element("p", "Model performance could not load."), retry);
    } finally {
        if (current === request) diagnostics.setAttribute("aria-busy", "false");
    }
}

competitionPicker.addEventListener("change", () => {
    const competition = competitionPicker.value;
    const url = new URL(window.location.href);
    url.searchParams.set("competition", competition);
    history.replaceState(null, "", url);
    loadPerformance(competition);
});

loadPerformance(new URLSearchParams(window.location.search).get("competition") || "UCL");
