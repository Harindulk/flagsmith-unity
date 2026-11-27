#!/usr/bin/env bash
# Flagsmith Experimentation through the Flagsmith CLI (v2). Run from the folder that holds flagsmith.json.
#
#   experiment.sh create  <feature> <control> <variant> <event> "<hypothesis>"   # flag, metric, experiment, start
#   experiment.sh status  <feature>                                              # state and hypothesis
#   experiment.sh results <feature>                                              # refresh, then rates, lift, chance to win
#   experiment.sh ship    <feature> <variant|control>                            # end it and serve the winner to everyone
#
# Writes need FLAGSMITH_YES=1, set only after the user approved. Production-like environments also need
# FLAGSMITH_CONFIRM_ENV=<exact environment name>.
set -euo pipefail

fs() { flagsmith --no-input "$@"; }
die() { echo "error: $*" >&2; exit 1; }
name_ok() { [[ "$1" =~ ^[A-Za-z0-9_.-]+$ ]] || die "'$1' must use only letters, digits, _ . -"; }

cmd=${1:-}
[ -n "$cmd" ] || { sed -n '2,10p' "$0"; exit 1; }
[ $# -ge 2 ] || die "usage: $cmd <feature> ..."
feature=$2; name_ok "$feature"

ENV=$(fs config --json --jq '.environment.value // empty' 2>/dev/null || true)
[ -n "$ENV" ] || die "no environment bound: run 'flagsmith init' in the project root first"
ENV_NAME=$(fs config --json --jq '.environment.name // empty')
BASE="api/v1/environments/$ENV"

confirm() {
  echo "About to: $1 (environment: $ENV_NAME)"
  local lc; lc=$(tr '[:upper:]' '[:lower:]' <<<"$ENV_NAME")
  [ "${FLAGSMITH_YES:-}" = "1" ] || die "nothing changed. Re-run with FLAGSMITH_YES=1 once the user has approved this."
  if [[ "$lc" =~ prod|prd|live ]] && [ "${FLAGSMITH_CONFIRM_ENV:-}" != "$ENV_NAME" ]; then
    die "'$ENV_NAME' looks like production. Re-run with FLAGSMITH_CONFIRM_ENV='$ENV_NAME' only if the user explicitly asked for it."
  fi
}

# Newest experiment on the feature; "open" skips completed ones.
experiment() {
  local filter='.'; [ "${2:-}" = open ] && filter='select(.status != "completed")'
  fs api "$BASE/experiments/?page_size=100" \
    --jq "[.results[] | select(.feature.name == \"$1\") | $filter] | sort_by(.id) | last | if . then \"\(.id) \(.status)\" else empty end"
}

case "$cmd" in
create)
  [ $# -eq 6 ] || die "usage: create <feature> <control> <variant> <event> \"<hypothesis>\""
  control=$3 variant=$4 event=$5 hypothesis=$6
  name_ok "$control"; name_ok "$variant"; name_ok "$event"
  read -r existing state <<<"$(experiment "$feature" open)"

  if [ -z "${existing:-}" ]; then
    confirm "create flag $feature ($control vs $variant), a metric on '$event', a 50/50 experiment with 100% enrolment, start it, and connect an event store if this self-hosted environment has none"
    fs feature get "$feature" >/dev/null 2>&1 || fs feature create "$feature" --value "$control" --enabled \
      --description "A/B test: $hypothesis" >/dev/null
    fs feature variant list "$feature" --jq '.[].key' | grep -Fqx "$variant" || \
      fs feature variant add "$feature" --value "$variant" --key "$variant" --weight 0 >/dev/null
    option=$(fs feature variant list "$feature" --jq ".[] | select(.key == \"$variant\") | .id")

    # Self-hosted: events need a store. Flagsmith's own database is enough. (Cloud manages this itself.)
    if [ "$(fs api "$BASE/warehouse-connections/" --jq 'if type == "array" then length else (.results | length) end' 2>/dev/null || echo skip)" = 0 ]; then
      fs api "$BASE/warehouse-connections/" -X POST --input - >/dev/null \
        <<<'{"warehouse_type":"postgres","name":"Flagsmith database","config":{"source":"flagsmith_database"}}'
    fi

    feature_id=$(fs feature get "$feature" --jq .id)
    existing=$(jq -n --arg n "$feature" --arg h "$hypothesis" --arg c "$control" --argjson f "$feature_id" --argjson o "$option" \
      '{name:$n, feature:$f, hypothesis:$h, experiment_rollout:{enabled:true, rollout_percentage:100,
        feature_state_value:{type:"string", value:$c},
        multivariate_feature_state_values:[{multivariate_feature_option:$o, percentage_allocation:50}]}}' |
      fs api "$BASE/experiments/" -X POST --input - --jq .id)
    state=created
  elif [ "$state" != created ]; then
    die "$feature already has a $state experiment ($existing). Use results or ship."
  else
    confirm "finish setting up experiment $existing on $feature (attach the '$event' metric if missing) and start it"
  fi

  metric=$(fs api "$BASE/experiment-metrics/" --jq "[(if type == \"array\" then .[] else .results[] end) | select(.definition.event == \"$event\")][0].id // empty")
  [ -n "$metric" ] || metric=$(jq -n --arg e "$event" '{name:$e, aggregation:"occurrence", direction:"up", definition:{version:1, event:$e}}' |
    fs api "$BASE/experiment-metrics/" -X POST --input - --jq .id)
  attached=$(fs api "$BASE/experiments/$existing/metrics/" --jq "[(if type == \"array\" then .[] else .results[] end) | select(.metric == $metric)] | length")
  [ "$attached" != 0 ] || fs api "$BASE/experiments/$existing/metrics/" -X POST --input - >/dev/null \
    <<<"{\"metric\":$metric,\"expected_direction\":\"increase\"}"
  fs api "$BASE/experiments/$existing/start/" -X POST --input - >/dev/null <<<'{}'
  echo "experiment $existing running: $feature = $control (control) vs $variant, metric '$event'"
  ;;
status)
  read -r id _ <<<"$(experiment "$feature")"; [ -n "$id" ] || die "no experiment on $feature"
  fs api "$BASE/experiments/$id/" --jq '{id, name, status, hypothesis}'
  ;;
results)
  read -r id _ <<<"$(experiment "$feature")"; [ -n "$id" ] || die "no experiment on $feature"
  # Refresh requests are rate-limited; a 429 just means a refresh is already queued.
  fs api "$BASE/experiments/$id/exposures/refresh/" -X POST --input - <<<'{}' >/dev/null 2>&1 || true
  fs api "$BASE/experiments/$id/results/refresh/" -X POST --input - <<<'{}' >/dev/null 2>&1 || true
  fs api "$BASE/experiments/$id/results/" --jq '
    def pct: if . == null then null else (. * 1000 | round) / 10 end;
    .results | {as_of, metrics: [.payload.metrics[]? | {
      metric_id,
      players: (.variants | map_values({n, conversions: .sum, rate_pct: (if .n > 0 then (.sum / .n | pct) else null end)})),
      vs_control: (.inference | map_values({lift_pct: (.lift | pct), chance_to_win_pct: (.chance_to_win | pct),
        interval_pct: [(.ci_low | pct), (.ci_high | pct)], conclusive: (.ci_low > 0 or .ci_high < 0)}))}]}'
  ;;
ship)
  [ $# -eq 3 ] || die "usage: ship <feature> <variant|control>"
  winner=$3; name_ok "$winner"
  read -r id state <<<"$(experiment "$feature" open)"; [ -n "$id" ] || die "no running experiment on $feature"
  if [ "$winner" = control ]; then body='{"rollout_decision":"control"}'
  else
    option=$(fs feature variant list "$feature" --jq ".[] | select(.key == \"$winner\") | .id")
    [ -n "$option" ] || die "$feature has no variant with key $winner"
    body="{\"rollout_decision\":\"variant\",\"rollout_variant\":$option}"
  fi
  confirm "complete experiment $id and serve '$winner' to everyone through $feature"
  fs api "$BASE/experiments/$id/complete/" -X POST --input - --jq '{name, status}' <<<"$body"
  ;;
*) die "unknown command '$cmd' (create, status, results, ship)" ;;
esac
