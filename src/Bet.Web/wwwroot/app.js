"use strict";

const $ = (selector) => document.querySelector(selector);
const state = {
    competition: new URLSearchParams(window.location.search).get("competition") || "UCL",
    view: "upcoming",
    market: "h2h",
    competitions: [],
    competitionSearch: "",
    expandedCompetitionGroups: new Set(),
    dashboards: new Map(),
    legs: [],
    slipMode: "across",
    sameGameLegs: [],
    sameGameEstimate: null,
    openMarkets: null,
    matchMarketData: new Map(),
    pending: false,
    problem: "",
    request: 0,
};

const formats = {
    date: new Intl.DateTimeFormat("en-AU", { weekday: "long", month: "short", day: "numeric", year: "numeric" }),
    time: new Intl.DateTimeFormat("en-AU", { hour: "numeric", minute: "2-digit" }),
    stamp: new Intl.DateTimeFormat("en-AU", { month: "short", day: "numeric", hour: "numeric", minute: "2-digit" }),
    freshness: new Intl.DateTimeFormat("en-AU", { year: "numeric", month: "short", day: "numeric", hour: "numeric", minute: "2-digit" }),
    percent: new Intl.NumberFormat("en-AU", { style: "percent", maximumFractionDigits: 1 }),
    signedPercent: new Intl.NumberFormat("en-AU", { style: "percent", maximumFractionDigits: 1, signDisplay: "exceptZero" }),
    decimal: new Intl.NumberFormat("en-AU", { minimumFractionDigits: 2, maximumFractionDigits: 2 }),
};
const modes = { live: "Provider feed", openliga: "OpenLigaDB scores", sqlserver: "SQL Server fixtures", snapshot: "UEFA snapshot", demo: "Demo data", unavailable: "Feed unavailable" };
const outcomeLabels = { home: "Home", draw: "Draw", away: "Away", over: "Over 2.5", under: "Under 2.5", yes: "Yes", no: "No" };
const competitionIcons = { UCL: "✦", EPL: "PL", LALIGA: "LL", BUNDESLIGA: "BL", SERIEA: "SA", LIGUE1: "L1", "A-LEAGUE": "AL", WC: "WC" };
const competitionTitles = { UCL: "UEFA Champions League" };
const competitionGroups = ["Continental clubs", "Domestic leagues", "Domestic cups", "National teams", "International competitions"];
const maxCompetitionsPerGroup = 12;
const maxQuoteAgeMs = 15 * 60 * 1000;
const marketRefreshMs = 5 * 60 * 1000;
const maxSameGameLegs = 12;

function el(tag, className, content) {
    const element = document.createElement(tag);
    if (className) element.className = className;
    if (content !== undefined) element.textContent = content;
    return element;
}

function number(value) {
    return typeof value === "number" && Number.isFinite(value);
}

function probability(value) {
    return number(value) && value >= 0 && value <= 1;
}

function validPrice(value) {
    return number(value) && value > 1;
}

function dateValue(value) {
    if (!value) return null;
    const date = new Date(value);
    return Number.isNaN(date.valueOf()) ? null : date;
}

function dateLabel(formatter, value, fallback = "—") {
    const date = dateValue(value);
    return date ? formatter.format(date) : fallback;
}

function kickoffTime(match) {
    return dateValue(match.kickoff)?.valueOf() ?? 0;
}

function canSelect(match) {
    return match.status === "upcoming" && kickoffTime(match) > Date.now();
}

function externalLink(url, label) {
    if (!url) return null;
    try {
        const parsed = new URL(url);
        if (parsed.protocol !== "https:") return null;
        const link = el("a", null, label);
        link.href = parsed.href;
        link.target = "_blank";
        link.rel = "noopener noreferrer";
        return link;
    } catch {
        return null;
    }
}

function emptyState(title, description) {
    const panel = el("div", "empty-state");
    panel.append(el("strong", null, title), el("span", null, description));
    return panel;
}

function dashboard() {
    return state.dashboards.get(state.competition);
}

async function fetchDashboard(competition) {
    const params = new URLSearchParams({ competition });
    const response = await fetch(`/api/dashboard?${params}`, { cache: "no-store", signal: AbortSignal.timeout(45000) });
    if (!response.ok) throw new Error(`Dashboard returned HTTP ${response.status}.`);
    return response.json();
}

async function loadDashboard({ updateSelections = false } = {}) {
    const request = ++state.request;
    const competition = state.competition;
    state.pending = true;
    state.problem = "";
    $("#refresh").disabled = true;
    $("#fixtures").setAttribute("aria-busy", "true");
    if (!dashboard()) {
        const loading = el("div", "loading-state", "Loading matches and forecasts…");
        loading.setAttribute("role", "status");
        $("#fixtures").replaceChildren(loading);
    }

    try {
        const data = await fetchDashboard(competition);
        if (request !== state.request) return;
        state.dashboards.set(competition, data);
        if (data.competitions?.length) state.competitions = data.competitions;

        if (updateSelections) {
            const otherCompetitions = new Set([...state.legs, ...state.sameGameLegs].map((leg) => leg.competition));
            otherCompetitions.delete(competition);
            const updates = await Promise.allSettled([...otherCompetitions].map(async (id) => [id, await fetchDashboard(id)]));
            if (request !== state.request) return;
            for (const update of updates) {
                if (update.status === "fulfilled") {
                    state.dashboards.set(...update.value);
                } else {
                    state.problem = "Some multi selections still use data from the previous refresh.";
                }
            }
        }
    } catch {
        if (request !== state.request) return;
        state.problem = dashboard()
            ? "Refresh failed. Showing the last data received for this competition."
            : "The dashboard could not load. Use Refresh to try again.";
    } finally {
        if (request === state.request) {
            state.pending = false;
            $("#refresh").disabled = false;
            $("#fixtures").setAttribute("aria-busy", "false");
            renderDashboard();
        }
    }
}

function renderDashboard() {
    const focusKey = document.activeElement?.dataset.focusKey;
    const data = dashboard();
    const mode = data?.mode || (state.pending ? "" : "unavailable");
    const badge = $("#data-mode");
    badge.className = ["data-mode", mode].filter(Boolean).join(" ");
    badge.lastElementChild.textContent = modes[mode] || "Connecting";

    const competition = data?.competition ?? state.competitions.find(({ id }) => id === state.competition);
    $("#competition-title").textContent = competitionTitles[state.competition] || competition?.name || "Football forecasts";
    const country = countryLabel(competition);
    $("#competition-region").textContent = country;
    $("#competition-region").hidden = !country;
    if (state.competitions.length) renderCompetitions();

    const feedLabel = { snapshot: "Snapshot dated", sqlserver: "SQL data as of" }[mode] || "Feed fetched";
    $("#fixtures-meta").textContent = data?.fixturesFetchedAt
        ? `${feedLabel} ${dateLabel(formats.freshness, data.fixturesFetchedAt)}`
        : "Fixture timestamp unavailable";
    const forecastMeta = $("#forecast-meta");
    forecastMeta.hidden = !data?.forecastAsOf;
    forecastMeta.textContent = data?.forecastAsOf ? `Forecast as of ${dateLabel(formats.freshness, data.forecastAsOf)}` : "";
    const resultMeta = $("#result-meta");
    resultMeta.hidden = !data?.latestForecastResultKickoff;
    resultMeta.textContent = data?.latestForecastResultKickoff
        ? `Latest result used · kickoff ${dateLabel(formats.freshness, data.latestForecastResultKickoff)}`
        : "";
    $("#model-jump").href = `/ModelPerformance?${new URLSearchParams({ competition: state.competition })}`;
    const storage = $("#storage-meta");
    storage.hidden = !data?.storage;
    if (data?.storage) {
        storage.textContent = data.storage.mode === "sqlserver" ? "SQL Server" : "SQL Server unavailable";
        storage.title = data.storage.message || "";
    }
    renderNotices(data);
    renderSlip();
    renderFixtures();
    renderSuggestions(data?.suggestedMultiples ?? []);

    if (focusKey) {
        restoreFocus(focusKey);
    }
}

function restoreFocus(key) {
    if (key) {
        [...document.querySelectorAll("[data-focus-key]")]
            .find((element) => element.dataset.focusKey === key)?.focus({ preventScroll: true });
    }
}

function renderNotices(data) {
    const notices = [];
    const source = externalLink(data?.sourceUrl, data?.mode === "openliga" ? "OpenLigaDB · ODbL" : "Fixture source");
    const odds = data?.oddsFetchedAt ? externalLink(data.oddsSourceUrl, "Odds source") : null;
    if (state.problem) notices.push(el("p", "notice warning", state.problem));
    if (data?.mode === "demo") notices.push(el("p", "notice warning", "Illustrative data for testing the dashboard and multi slip."));
    if (data?.mode === "snapshot") {
        const note = el("p", "notice", "Local UEFA snapshot. Forecasts use the included results through the snapshot date.");
        if (source) note.append(source);
        if (odds && data.oddsFetchedAt) note.append(odds);
        notices.push(note);
    } else if (source || odds) {
        const note = el("p", "notice", data?.mode === "openliga" ? "Community score updates" : "Data providers");
        if (source) note.append(source);
        if (odds) note.append(odds);
        notices.push(note);
    }
    for (const warning of data?.warnings ?? []) {
        if (!/odds|bookmaker|sportsbook/i.test(warning)) notices.push(el("p", "notice warning", warning));
    }
    $("#notices").replaceChildren(...notices);
}

function countryLabel(competition) {
    return competition?.country && competition.country !== "World" ? competition.country : "";
}

function searchText(value) {
    return value.normalize("NFD").replace(/\p{M}/gu, "").toLowerCase();
}

function competitionButton(competition) {
    const active = competition.id === state.competition;
    const button = el("button", active ? "competition-button active" : "competition-button");
    button.type = "button";
    button.dataset.focusKey = `competition:${competition.id}`;
    if (active) button.setAttribute("aria-current", "true");
    const icon = el("span", "competition-icon", competitionIcons[competition.id] || "◈");
    icon.setAttribute("aria-hidden", "true");
    const label = el("span", null, competition.name);
    const country = countryLabel(competition);
    const details = [country, competition.keyFree ? "Open feed" : ""].filter(Boolean).join(" · ");
    if (details) label.append(el("span", "competition-country", details));
    button.append(icon, label);
    button.addEventListener("click", () => {
        if (state.competition === competition.id) return;
        state.competition = competition.id;
        const url = new URL(window.location.href);
        url.searchParams.set("competition", competition.id);
        history.replaceState(null, "", url);
        state.problem = "";
        renderDashboard();
        loadDashboard();
    });
    return button;
}

function renderCompetitions() {
    const nav = $("#competition-nav");
    const search = searchText(state.competitionSearch).trim().split(/\s+/).filter(Boolean);
    const grouped = new Map(competitionGroups.map((group) => [group, []]));
    let matches = 0;
    for (const competition of state.competitions) {
        const text = searchText([competition.name, competition.country, competition.id].join(" "));
        if (!search.every((term) => text.includes(term))) continue;
        const group = competition.group || "Other competitions";
        if (!grouped.has(group)) grouped.set(group, []);
        grouped.get(group).push(competition);
        matches += 1;
    }

    const groups = [...grouped].filter(([, competitions]) => competitions.length > 0);
    const sections = groups.map(([group, competitions], index) => {
        const section = el("section", "competition-group");
        const heading = el("h3", null, group);
        heading.id = `competition-group-${index}`;
        section.setAttribute("aria-labelledby", heading.id);
        const list = el("div", "competition-items");
        const expanded = state.expandedCompetitionGroups.has(group);
        const shown = expanded ? competitions : competitions.slice(0, maxCompetitionsPerGroup);
        const selected = competitions.find(({ id }) => id === state.competition);
        if (selected && !shown.includes(selected)) shown[shown.length - 1] = selected;
        list.append(...shown.map(competitionButton));
        if (competitions.length > maxCompetitionsPerGroup) {
            const showMore = el("button", "competition-more", expanded ? "Show fewer" : `Show ${competitions.length - shown.length} more`);
            showMore.type = "button";
            showMore.dataset.focusKey = `group:${group}`;
            showMore.setAttribute("aria-label", `${showMore.textContent} in ${group}`);
            showMore.setAttribute("aria-expanded", String(expanded));
            showMore.addEventListener("click", () => {
                if (expanded) state.expandedCompetitionGroups.delete(group);
                else state.expandedCompetitionGroups.add(group);
                renderCompetitions();
                const firstNew = nav.querySelectorAll(".competition-group")[index].querySelectorAll(".competition-button")[maxCompetitionsPerGroup];
                if (!expanded && firstNew) {
                    firstNew.focus();
                } else {
                    nav.querySelectorAll(".competition-group")[index].querySelector(".competition-more")?.focus();
                }
            });
            list.append(showMore);
        }
        section.append(heading, list);
        return section;
    });
    const left = nav.scrollLeft;
    const top = nav.scrollTop;
    nav.replaceChildren(...(sections.length ? sections : [el("p", "competition-no-results", "No competitions match your search.")]));
    nav.scrollLeft = left;
    nav.scrollTop = top;
    $("#competition-search").disabled = false;
    const result = $("#competition-results");
    const summary = search.length ? `${matches} matching ${matches === 1 ? "competition" : "competitions"}` : "";
    if (result.textContent !== summary) result.textContent = summary;
}

function renderFixtures() {
    const data = dashboard();
    const fixtures = $("#fixtures");
    $("#market-select").disabled = state.view === "finished";
    if (!data) {
        fixtures.replaceChildren(emptyState("Fixtures unavailable", state.problem || "Fetching fixtures for this competition."));
        return;
    }

    const matches = (data.matches ?? []).filter((match) => {
        if (state.view === "all") return true;
        if (state.view === "finished") return match.status === "finished";
        return match.status === "live" || canSelect(match) || (match.status === "unconfirmed" && kickoffTime(match) > Date.now()) ||
            (match.status === "pending" && Date.now() - kickoffTime(match) < 3 * 60 * 60 * 1000);
    }).sort((a, b) => state.view === "finished" ? kickoffTime(b) - kickoffTime(a) : kickoffTime(a) - kickoffTime(b));

    if (!matches.length) {
        const title = state.view === "finished" ? "No final scores available" : "No fixtures to show";
        const description = data.mode === "unavailable"
            ? "The fixture feed is unavailable for this competition."
            : "Try another fixture view or select a different competition.";
        fixtures.replaceChildren(emptyState(title, description));
        return;
    }

    const openQuotes = new Set([...fixtures.querySelectorAll(".match-quotes[open]")].map((item) => item.dataset.fixture));
    const byDate = new Map();
    for (const match of matches) {
        const label = dateLabel(formats.date, match.kickoff, "Kickoff date pending");
        if (!byDate.has(label)) byDate.set(label, []);
        byDate.get(label).push(match);
    }
    fixtures.replaceChildren(...[...byDate].map(([label, fixturesOnDate]) => {
        const group = el("section", "fixture-group");
        const heading = el("header", "fixture-group-header");
        const count = fixturesOnDate.length;
        heading.append(el("h3", null, label), el("span", null, `${count} ${count === 1 ? "match" : "matches"}`));
        group.append(heading, ...fixturesOnDate.map((match) => renderMatch(match, openQuotes.has(String(match.id)))));
        return group;
    }));
}

function teamLine(name, score) {
    const initials = String(name).split(/[^\p{L}\p{N}]+/u).filter(Boolean);
    const short = initials.length > 1 ? initials.slice(0, 3).map((part) => part[0]).join("") : initials[0]?.slice(0, 3);
    const line = el("div", "team-line");
    const badge = el("span", "team-badge", short?.toUpperCase() || "•");
    badge.setAttribute("aria-hidden", "true");
    line.append(badge, el("span", "team-name", name));
    if (number(score)) line.append(el("strong", "score", score));
    return line;
}

function renderMatch(match, showQuotes) {
    const article = el("article", "match-card");
    article.setAttribute("aria-label", `${match.home} v ${match.away}`);
    const top = el("div", "match-top");
    const time = el("div", "match-time");
    if (match.status !== "unconfirmed") {
        const clock = el("time", null, dateLabel(formats.time, match.kickoff, "Kickoff pending"));
        if (dateValue(match.kickoff)) clock.dateTime = match.kickoff;
        time.append(clock);
    }
    const statuses = { live: "In play", finished: "Final", postponed: "Postponed", cancelled: "Cancelled", unconfirmed: "Kickoff unconfirmed", pending: "Awaiting score" };
    const status = statuses[match.status] || (canSelect(match) ? "" : "Awaiting status");
    if (status) time.append(el("span", match.status === "live" ? "live-label" : null, status));
    const teams = el("div", "teams");
    teams.append(teamLine(match.home, match.homeScore), teamLine(match.away, match.awayScore));
    top.append(time, teams);
    const market = match.markets?.find(({ id }) => id === state.market);
    const selectable = canSelect(match);
    const usableOutcomes = market?.outcomes?.some((outcome) => probability(outcome.probability) || quoteOptions(outcome).length);

    if (selectable && usableOutcomes) {
        const outcomes = el("div", "outcomes");
        outcomes.setAttribute("role", "group");
        outcomes.setAttribute("aria-label", market.name);
        const estimates = market.outcomes.filter((outcome) => probability(outcome.probability));
        const topChance = Math.max(...estimates.map((outcome) => outcome.probability));
        const leaders = estimates.filter((outcome) => outcome.probability === topChance);
        for (const outcome of market.outcomes) outcomes.append(renderOutcome(match, market, outcome, leaders.length === 1 && leaders[0] === outcome));
        top.append(outcomes);
    } else if (selectable) {
        top.append(el("p", "match-message", "No forecasts or prices available for this market."));
    } else {
        const message = match.status === "finished" ? "Full-time score" : "Pre-match markets unavailable";
        top.append(el("p", "match-message", message));
    }
    article.append(top);

    if (selectable && number(match.expectedHomeGoals) && number(match.expectedAwayGoals)) {
        const goals = `Projected goals · ${formats.decimal.format(match.expectedHomeGoals)} home · ${formats.decimal.format(match.expectedAwayGoals)} away`;
        const scorelines = match.topScorelines?.filter(({ homeGoals, awayGoals, probability: chance }) => number(homeGoals) && number(awayGoals) && probability(chance));
        const scores = scorelines?.map((line) => `${line.homeGoals}–${line.awayGoals} (${formats.percent.format(line.probability)})`).join(", ");
        article.append(el("p", "match-projections", scores ? `${goals} · Likely scores ${scores}` : goals));
    }

    if (selectable && usableOutcomes && market.outcomes.some((outcome) => quoteOptions(outcome).length)) {
        const quotes = el("details", "match-quotes");
        quotes.dataset.fixture = match.id;
        quotes.open = showQuotes;
        const list = el("div", "quote-list");
        for (const outcome of market.outcomes) {
            const prices = quoteOptions(outcome).map((quote) => {
                const label = `${quote.bookmaker} ${formats.decimal.format(quote.price)}`;
                return quote.updatedAt ? `${label} (as of ${dateLabel(formats.stamp, quote.updatedAt)})` : label;
            });
            if (!prices.length) continue;
            const row = el("div", "quote-row");
            row.append(el("strong", null, outcomeName(market, outcome)), el("span", null, prices.join(" · ")));
            list.append(row);
        }
        quotes.append(el("summary", null, "Compare bookmaker prices"), list);
        article.append(quotes);
    }
    if (selectable) article.append(renderMatchMarkets(match));
    return article;
}

function outcomeName(market, outcome) {
    return ["h2h", "totals25", "btts"].includes(market.id) ? outcomeLabels[outcome.id] || outcome.name : outcome.name;
}

function selectionName(match, market, outcome) {
    if (market.id === "h2h" && outcome.id === "home") return match.home;
    if (market.id === "h2h" && outcome.id === "away") return match.away;
    return outcomeName(market, outcome);
}

function fixtureKey({ competition, matchId }) {
    return `${competition}:${matchId}`;
}

function browserMarkets(match, fixture) {
    const expanded = state.matchMarketData.get(fixtureKey(fixture))?.data?.markets ?? [];
    const combined = new Map((match.markets ?? []).map((market) => [market.id, market]));
    for (const market of expanded) {
        const mainMarket = combined.get(market.id);
        if (!mainMarket) {
            combined.set(market.id, market);
            continue;
        }
        const mainOutcomes = new Map(mainMarket.outcomes.map((outcome) => [outcome.id, outcome]));
        combined.set(market.id, {
            ...mainMarket,
            ...market,
            outcomes: market.outcomes.map((outcome) => {
                const current = mainOutcomes.get(outcome.id);
                if (!current) return outcome;
                const quotes = new Map(quoteOptions(outcome).map((quote) => [quote.bookmaker, quote]));
                for (const quote of quoteOptions(current)) {
                    if (!quotes.has(quote.bookmaker) || dateValue(quote.updatedAt) >= dateValue(quotes.get(quote.bookmaker).updatedAt)) {
                        quotes.set(quote.bookmaker, quote);
                    }
                }
                return { ...outcome, probability: current.probability, quotes: [...quotes.values()].sort((a, b) => b.price - a.price) };
            }),
        });
    }
    return [...combined.values()];
}

async function loadMatchMarkets(fixture, { force = false } = {}) {
    const key = fixtureKey(fixture);
    const previous = state.matchMarketData.get(key);
    if (previous?.pending || (!force && previous && Date.now() - previous.requestedAt < marketRefreshMs)) return;
    const entry = { requestedAt: Date.now(), pending: true, data: previous?.data, error: "" };
    state.matchMarketData.set(key, entry);
    redrawMarkets(fixture);
    try {
        const params = new URLSearchParams(fixture);
        const response = await fetch(`/api/match-markets?${params}`, { cache: "no-store", signal: AbortSignal.timeout(30000) });
        if (!response.ok) throw new Error(`Match markets returned HTTP ${response.status}.`);
        entry.data = await response.json();
    } catch {
        entry.error = "Match markets could not refresh. The latest available selections are shown below.";
    } finally {
        entry.pending = false;
        redrawMarkets(fixture);
    }
}

function redrawMarkets(fixture) {
    renderSlip();
    if (!state.openMarkets || !sameMatch(state.openMarkets, fixture) || state.competition !== fixture.competition) return;
    const focusKey = document.activeElement?.dataset.focusKey;
    renderFixtures();
    restoreFocus(focusKey);
}

function renderMatchMarkets(match) {
    const fixture = { competition: state.competition, matchId: match.id };
    const key = fixtureKey(fixture);
    const open = state.openMarkets && sameMatch(state.openMarkets, fixture);
    const wrap = el("div", "match-market-browser");
    const action = el("button", "market-browser-action", open ? "Hide match markets" : "Build Same Game Multi");
    action.type = "button";
    action.dataset.focusKey = `markets:${key}`;
    action.setAttribute("aria-expanded", String(Boolean(open)));
    action.setAttribute("aria-label", `${action.textContent} for ${match.home} v ${match.away}`);
    action.addEventListener("click", () => {
        state.openMarkets = open ? null : fixture;
        if (open) {
            renderFixtures();
        } else {
            setSlipMode("same");
            loadMatchMarkets(fixture);
        }
        restoreFocus(action.dataset.focusKey);
    });
    wrap.append(action);
    if (!open) return wrap;

    const panel = el("div", "market-browser-panel");
    const anchor = state.sameGameLegs[0];
    if (state.slipMode === "same" && anchor && !sameMatch(anchor, fixture)) {
        const button = el("button", "market-browser-action", "Start a new Same Game Multi here");
        button.type = "button";
        button.addEventListener("click", () => {
            state.sameGameLegs = [];
            updateSlip("Choose selections for this match.");
            restoreFocus(action.dataset.focusKey);
        });
        panel.append(el("p", "market-browser-note", "Your Same Game Multi uses another match."), button);
    }
    panel.append(el("p", "market-browser-note", "Choose outcomes from different markets in this match to estimate their joint chance."));
    const entry = state.matchMarketData.get(key);
    if (entry?.pending) {
        const status = el("p", "market-browser-note", "Loading match markets…");
        status.setAttribute("role", "status");
        panel.append(status);
    }
    for (const warning of entry?.data?.warnings ?? []) panel.append(el("p", "notice warning", warning));
    if (entry?.error) {
        const retry = el("button", "market-browser-action", "Retry match markets");
        retry.type = "button";
        retry.addEventListener("click", () => loadMatchMarkets(fixture, { force: true }));
        panel.append(el("p", "notice warning", entry.error), retry);
    }

    const groups = new Map();
    for (const market of browserMarkets(match, fixture)) {
        const group = market.group || (market.id === "h2h" ? "Match result" : "Goals");
        if (!groups.has(group)) groups.set(group, []);
        groups.get(group).push(market);
    }
    for (const [group, markets] of groups) {
        const section = el("section", "market-browser-group");
        section.append(el("h4", null, group));
        for (const market of markets) {
            const marketPanel = el("div", "market-browser-market");
            const outcomes = el("div", "market-browser-outcomes");
            outcomes.setAttribute("role", "group");
            outcomes.setAttribute("aria-label", market.name);
            for (const outcome of market.outcomes) outcomes.append(renderOutcome(match, market, outcome, false, "browser"));
            marketPanel.append(el("strong", null, market.name), outcomes);
            section.append(marketPanel);
        }
        panel.append(section);
    }
    if (!groups.size && !entry?.pending) panel.append(el("p", "market-browser-note", "No markets are available for this match."));
    wrap.append(panel);
    return wrap;
}

function renderOutcome(match, market, outcome, leading, surface = "fixture") {
    const button = el("button", "outcome");
    button.type = "button";
    const leg = { competition: state.competition, matchId: match.id, marketId: market.id, outcomeId: outcome.id };
    const quote = quoteOptions(outcome)[0];
    const forecast = probability(outcome.probability);
    const implied = quote && !forecast && probability(outcome.impliedProbability);
    const anchor = state.sameGameLegs[0];
    const differentMatch = state.slipMode === "same" && anchor && !sameMatch(anchor, leg);
    button.disabled = (!quote && !forecast) || differentMatch;
    button.dataset.focusKey = `outcome:${surface}:${leg.competition}:${leg.matchId}:${leg.marketId}:${leg.outcomeId}`;
    button.dataset.leg = JSON.stringify(leg);
    button.setAttribute("aria-pressed", String(activeLegs().some((selected) => sameLeg(selected, leg))));
    button.append(el("span", "outcome-name", outcomeName(market, outcome)));
    if (quote) {
        button.append(el("strong", "outcome-price", formats.decimal.format(quote.price)));
        if (forecast) button.append(el("span", "outcome-probability", `${formats.percent.format(outcome.probability)} model`));
        if (implied) button.append(el("span", "outcome-probability", `${formats.percent.format(outcome.impliedProbability)} price-implied`));
        button.append(el("span", "outcome-bookmaker", quote.bookmaker));
    } else if (forecast) {
        button.append(el("strong", "outcome-price", formats.percent.format(outcome.probability)), el("span", "outcome-probability", "Model chance"));
    } else {
        button.append(el("span", "outcome-price missing", "Unavailable"));
    }
    const notes = [selectionName(match, market, outcome)];
    if (quote) notes.push(`decimal odds ${formats.decimal.format(quote.price)} at ${quote.bookmaker}`);
    if (forecast) notes.push(`model probability ${formats.percent.format(outcome.probability)}`);
    if (implied) notes.push(`price-implied estimate ${formats.percent.format(outcome.impliedProbability)}`);
    if (leading) {
        button.append(el("span", "outcome-leader", "Top chance"));
        notes.push("highest model probability in this market");
    }
    button.setAttribute("aria-label", notes.join(", "));
    button.addEventListener("click", () => toggleLeg(leg));
    return button;
}

function sameMatch(left, right) {
    return left.competition === right.competition && left.matchId === right.matchId;
}

function sameLeg(left, right) {
    return sameMatch(left, right) && left.marketId === right.marketId && left.outcomeId === right.outcomeId;
}

function resolveLeg(leg) {
    const data = state.dashboards.get(leg.competition);
    const match = data?.matches?.find(({ id }) => id === leg.matchId);
    if (!match || !canSelect(match)) return null;
    const market = browserMarkets(match, leg).find(({ id }) => id === leg.marketId);
    const outcome = market?.outcomes?.find(({ id }) => id === leg.outcomeId);
    if (!outcome || (!probability(outcome.probability) && !quoteOptions(outcome).length)) return null;
    return { leg, match, market, outcome, mode: data.mode };
}

function activeLegs() {
    return state.slipMode === "same" ? state.sameGameLegs : state.legs;
}

function setActiveLegs(legs) {
    if (state.slipMode === "same") state.sameGameLegs = legs;
    else state.legs = legs;
}

function setSlipMode(mode) {
    state.slipMode = mode;
    for (const button of document.querySelectorAll("[data-slip-mode]")) {
        const active = button.dataset.slipMode === mode;
        button.classList.toggle("active", active);
        button.setAttribute("aria-pressed", String(active));
    }
    renderSlip();
    renderFixtures();
    renderSuggestions(dashboard()?.suggestedMultiples ?? []);
}

function toggleLeg(leg) {
    const picked = resolveLeg(leg);
    if (!picked) {
        updateSlip("This market is no longer available.");
        return;
    }
    const legs = activeLegs();
    const sameGame = state.slipMode === "same";
    if (sameGame && legs.length && !sameMatch(legs[0], leg)) {
        updateSlip("Choose an outcome from the match already in this slip.");
        return;
    }
    const replace = (item) => sameMatch(item, leg) && (!sameGame || item.marketId === leg.marketId);
    const previous = legs.find(replace);
    if (previous && sameLeg(previous, leg)) {
        setActiveLegs(legs.filter((item) => !sameLeg(item, leg)));
        updateSlip("Selection removed.");
        return;
    }

    const other = legs.filter((item) => !replace(item));
    if (sameGame && other.length >= maxSameGameLegs) {
        updateSlip(`A Same Game Multi can contain up to ${maxSameGameLegs} selections.`);
        return;
    }
    if (other.map(resolveLeg).filter(Boolean).some((item) => (item.mode === "demo") !== (picked.mode === "demo"))) {
        updateSlip("Demo selections can only be combined with other demo selections.");
        return;
    }
    setActiveLegs([...other, leg]);
    updateSlip(previous ? "Previous selection replaced." : "Selection added.");
}

function updateSlip(message) {
    const focusKey = document.activeElement?.dataset.focusKey;
    renderSlip();
    $("#slip-status").textContent = message;
    if (state.slipMode === "same") {
        renderFixtures();
        restoreFocus(focusKey);
        return;
    }
    for (const button of document.querySelectorAll(".outcome[data-leg]")) {
        const item = JSON.parse(button.dataset.leg);
        button.setAttribute("aria-pressed", String(activeLegs().some((leg) => sameLeg(item, leg))));
    }
}

function quoteOptions(outcome) {
    const cutoff = Date.now() - maxQuoteAgeMs;
    return (outcome.quotes ?? []).filter(({ bookmaker, price, updatedAt }) => {
        const updated = dateValue(updatedAt);
        return bookmaker && validPrice(price) && updated && updated.valueOf() >= cutoff;
    });
}

function combinedQuote(resolved) {
    const firstQuotes = quoteOptions(resolved[0].outcome);
    let best = null;
    for (const first of firstQuotes) {
        const prices = resolved.map(({ outcome }) => quoteOptions(outcome)
            .filter((quote) => quote.bookmaker.toLowerCase() === first.bookmaker.toLowerCase())
            .reduce((max, quote) => Math.max(max, quote.price), 0));
        if (prices.some((price) => !price)) continue;
        const price = prices.reduce((product, next) => product * next, 1);
        if (validPrice(price) && (!best || price > best.price)) best = { price, bookmaker: first.bookmaker };
    }
    return best;
}

function slipTotal(label, value) {
    const row = el("div", "slip-total");
    row.append(el("span", null, label), el("strong", null, value));
    return row;
}

function loadJointEstimate(resolved) {
    const first = resolved[0].leg;
    const legs = resolved.map(({ leg }) => ({ marketId: leg.marketId, outcomeId: leg.outcomeId }));
    const history = state.dashboards.get(first.competition)?.historyFetchedAt;
    const key = JSON.stringify([first.competition, first.matchId, history, legs]);
    if (state.sameGameEstimate?.key === key) return state.sameGameEstimate;
    state.sameGameEstimate?.controller.abort();
    const controller = new AbortController();
    const entry = { key, controller, pending: true, value: null, failed: false };
    state.sameGameEstimate = entry;
    fetch("/api/same-game", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ competition: first.competition, matchId: first.matchId, legs }),
        signal: AbortSignal.any([controller.signal, AbortSignal.timeout(30000)]),
    }).then(async (response) => {
        if (!response.ok) throw new Error(`Joint estimate returned HTTP ${response.status}.`);
        entry.value = await response.json();
    }).catch(() => {
        entry.failed = true;
    }).finally(() => {
        entry.pending = false;
        if (state.sameGameEstimate === entry && state.slipMode === "same") renderSlip();
    });
    return entry;
}

function renderJointEstimate(totals, resolved) {
    const estimate = loadJointEstimate(resolved);
    const chance = estimate.value?.probability;
    const value = estimate.pending ? "Calculating…" : probability(chance) ? formats.percent.format(chance) : "Unavailable";
    totals.append(slipTotal("Joint model chance", value));
    if (estimate.pending) return;
    const note = estimate.failed ? "The joint estimate could not load." : estimate.value?.reason;
    if (note) totals.append(el("p", "slip-note", note));
    if (estimate.failed) {
        const retry = el("button", "slip-clear", "Retry estimate");
        retry.type = "button";
        retry.addEventListener("click", () => {
            state.sameGameEstimate = null;
            renderSlip();
        });
        totals.append(retry);
    }
    if (probability(chance) && chance > 0) totals.append(el("p", "slip-note", "The model adds up scorelines where all selections win."));
}

function renderSlip() {
    const selected = activeLegs();
    const sameGame = state.slipMode === "same";
    const resolved = selected.map(resolveLeg).filter(Boolean);
    if (resolved.length !== selected.length) $("#slip-status").textContent = "An expired or unavailable selection was removed.";
    setActiveLegs(resolved.map(({ leg }) => leg));
    const count = $("#slip-count");
    count.textContent = resolved.length;
    count.setAttribute("aria-label", `${resolved.length} ${resolved.length === 1 ? "selection" : "selections"}`);
    $("#slip-jump-count").textContent = resolved.length;

    const content = $("#slip-content");
    if (!resolved.length) {
        const empty = el("div", "slip-empty");
        const icon = document.querySelector(".slip-icon svg").cloneNode(true);
        const hint = sameGame
            ? "Open Build Same Game Multi on a fixture, then choose two or more markets for that match."
            : "Choose an outcome on each of two or more upcoming matches to compare a multi.";
        empty.append(icon, el("strong", null, "Your slip is empty"), el("p", null, hint));
        content.replaceChildren(empty);
        return;
    }

    const legs = resolved.map(({ leg, match, market, outcome }) => {
        const row = el("div", "slip-leg");
        const copy = el("div");
        const label = selectionName(match, market, outcome);
        copy.append(el("strong", null, label), el("p", null, `${match.home} v ${match.away} · ${market.name}`));
        const figures = [];
        const best = quoteOptions(outcome)[0];
        if (probability(outcome.probability)) figures.push(`${formats.percent.format(outcome.probability)} model`);
        else if (best && probability(outcome.impliedProbability)) figures.push(`${formats.percent.format(outcome.impliedProbability)} price-implied`);
        if (best) figures.push(`${formats.decimal.format(best.price)} best listed price`);
        if (figures.length) copy.append(el("p", null, figures.join(" · ")));
        const remove = el("button", "slip-remove", "×");
        remove.type = "button";
        remove.dataset.focusKey = `remove:${leg.competition}:${leg.matchId}:${leg.marketId}`;
        remove.setAttribute("aria-label", `Remove ${label} from multi slip`);
        remove.addEventListener("click", () => {
            setActiveLegs(activeLegs().filter((item) => !sameLeg(item, leg)));
            updateSlip("Selection removed.");
            content.querySelector(".slip-remove")?.focus();
        });
        row.append(copy, remove);
        return row;
    });

    const totals = el("div", "slip-totals");
    if (sameGame && resolved.length >= 2) {
        renderJointEstimate(totals, resolved);
    } else if (resolved.length >= 2) {
        const estimates = resolved.map(({ outcome }) => outcome.probability);
        const chance = estimates.every(probability) ? estimates.reduce((product, next) => product * next, 1) : null;
        const quote = combinedQuote(resolved);
        totals.append(slipTotal("Combined model chance", probability(chance) ? formats.percent.format(chance) : "Unavailable"));
        if (quote) {
            totals.append(slipTotal("Indicative combined odds", formats.decimal.format(quote.price)));
            totals.append(slipTotal("Sportsbook", quote.bookmaker));
            if (probability(chance)) {
                totals.append(slipTotal("Estimated return per $1", formats.signedPercent.format(chance * quote.price - 1)));
            }
        }
        const note = ["The model multiplies the leg probabilities and assumes independent match outcomes."];
        if (quote) {
            note.push("The price is a product of pre-match quotes from the same sportsbook. Check its actual multi price and eligibility.");
        } else if (resolved.some(({ outcome }) => quoteOptions(outcome).length)) {
            note.push("No single sportsbook supplied prices for every leg.");
        }
        totals.append(el("p", "slip-note", note.join(" ")));
    } else {
        const hint = sameGame ? "Add a different market for this match to estimate the joint chance." : "Add a selection from another match to see multi estimates.";
        totals.append(el("p", "slip-empty-message slip-note", hint));
    }
    if (resolved.some(({ mode }) => mode === "demo")) totals.append(el("p", "slip-note", "This slip uses illustrative demo data."));
    const clear = el("button", "slip-clear", "Clear slip");
    clear.type = "button";
    clear.dataset.focusKey = "clear";
    clear.addEventListener("click", () => {
        setActiveLegs([]);
        updateSlip("Multi slip cleared.");
    });
    totals.append(clear);
    content.replaceChildren(...legs, totals);
}

function renderSuggestions(suggestions) {
    const panel = $("#multi-suggestions");
    if (state.slipMode === "same") {
        panel.hidden = true;
        return;
    }
    const items = suggestions.map((suggestion) => {
        const legs = suggestion.legs.map((leg) => ({ ...leg, competition: state.competition }));
        const resolved = legs.map(resolveLeg);
        if (resolved.length < 2 || resolved.some((item) => !item)) return null;
        const listedPrice = validPrice(suggestion.price) && suggestion.bookmaker;
        const fresh = listedPrice && resolved.every(({ outcome }) => quoteOptions(outcome).some(({ bookmaker }) => bookmaker === suggestion.bookmaker));
        if (listedPrice && !fresh) return null;
        const item = el("article", "suggestion");
        const title = el("h3", null, suggestion.name);
        const list = el("ul");
        for (const { match, market, outcome } of resolved) {
            const chance = probability(outcome.probability) ? ` · ${formats.percent.format(outcome.probability)} model` : "";
            list.append(el("li", null, `${selectionName(match, market, outcome)} · ${match.home} v ${match.away}${chance}`));
        }
        const figures = [];
        if (probability(suggestion.probability)) figures.push(`${formats.percent.format(suggestion.probability)} model chance`);
        if (listedPrice) {
            figures.push(`${formats.decimal.format(suggestion.price)} indicative at ${suggestion.bookmaker}`);
        }
        if (number(suggestion.expectedValue)) figures.push(`${formats.signedPercent.format(suggestion.expectedValue)} estimated return`);
        const load = el("button", null, "Load into slip");
        load.type = "button";
        load.dataset.focusKey = `suggestion:${suggestion.name}`;
        load.addEventListener("click", () => {
            state.legs = legs.filter(resolveLeg);
            updateSlip("Suggested multi loaded into the slip.");
        });
        item.append(title, list, el("p", null, figures.join(" · ")),
            el("p", null, "Match-result legs from this competition. Combined chance assumes independent results."), load);
        return item;
    }).filter(Boolean);
    panel.hidden = !items.length;
    $("#suggestions-content").replaceChildren(...items);
}

$("#refresh").addEventListener("click", () => loadDashboard({ updateSelections: true }));
$("#competition-search").addEventListener("input", (event) => {
    state.competitionSearch = event.target.value;
    $("#competition-nav").scrollTo(0, 0);
    renderCompetitions();
});
for (const button of document.querySelectorAll(".view-button")) {
    button.addEventListener("click", () => {
        state.view = button.dataset.view;
        for (const sibling of document.querySelectorAll(".view-button")) {
            sibling.classList.toggle("active", sibling === button);
            sibling.setAttribute("aria-pressed", String(sibling === button));
        }
        renderFixtures();
    });
}
$("#market-select").addEventListener("change", (event) => {
    state.market = event.target.value;
    renderFixtures();
});
for (const button of document.querySelectorAll("[data-slip-mode]")) {
    button.addEventListener("click", () => setSlipMode(button.dataset.slipMode));
}

const zone = Intl.DateTimeFormat().resolvedOptions().timeZone;
$("#timezone").textContent = zone ? `Kickoffs in ${zone.replaceAll("_", " ")}` : "Kickoffs in local time";
renderSlip();
loadDashboard();
window.setInterval(() => {
    if (!document.hidden && !state.pending) loadDashboard();
}, 60000);
