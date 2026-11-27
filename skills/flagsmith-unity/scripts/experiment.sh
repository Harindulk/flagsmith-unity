#!/usr/bin/env bash
# Flagsmith Experimentation through the Flagsmith CLI (v2). Run from the folder that holds flagsmith.json.
#
#   experiment.sh create  <feature> <control> <variant> <event> "<hypothesis>"   # flag, metric, experiment, start
#   experiment.sh status  <feature>                                              # state + enrolled per variant
#   experiment.sh results <feature>                                              # refresh and print lift / chance to win
#   experiment.sh ship    <feature> <variant|control>                            # end it and serve the winner to everyone
#
# Writes need confirmation unless FLAGSMITH_YES=1. Production-looking environments always ask.
set -euo pipefail

fs() { flagsmith --no-input "$@"; }
die() { echo "error: $*" >&2; exit 1; }

ENV=$(fs config --json --jq '.environment.value // .environment' 2>/dev/null || true)
[ -n "$ENV" ] && [ "$ENV" != "null" ] || ENV=$(jq -r .environment flagsmith.json 2>/dev/null || true)
[ -n "$ENV" ] && [ "$ENV" != "null" ] || die "no environment: run 'flagsmith init' first"
ENV_NAME=$(fs environment get "$ENV" --jq .name)
BASE="api/v1/environments/$ENV"

confirm() {
  echo "About to: $1 (environment: $ENV_NAME)"
  if [[ "$ENV_NAME" =~ [Pp]rod|[Ll]ive ]]; then
    [ "${FLAGSMITH_CONFIRM_ENV:-}" = "$ENV_NAME" ] || die "'$ENV_NAME' looks like production. Re-run with FLAGSMITH_CONFIRM_ENV='$ENV_NAME' after the user agrees."
  elif [ "${FLAGSMITH_YES:-}" != "1" ]; then
    die "re-run with FLAGSMITH_YES=1 once the user has approved this change"
  fi
}

experiment_id() {
  fs api "$BASE/experiments/" --jq "[.results[] | select(.feature.name == \"$1\" and .status != \"completed\")][0].id // empty"
}

case "${1:-}" in
create)
  [ $# -eq 6 ] || die "usage: create <feature> <control> <variant> <event> \"<hypothesis>\""
  feature=$2 control=$3 variant=$4 event=$5 hypothesis=$6
  [[ "$event" != \$* ]] || die "event names starting with \$ are reserved"
  [ -z "$(experiment_id "$feature")" ] || die "$feature already has an experiment"
  confirm "create $feature ($control vs $variant), metric on '$event', a 50/50 experiment, and start it"

  fs feature get "$feature" >/dev/null 2>&1 || fs feature create "$feature" --value "$control" --enabled \
    --description "A/B test: $hypothesis" >/dev/null
  fs feature variant list "$feature" --jq '.[].key' | grep -qx "$variant" || \
    fs feature variant add "$feature" --value "$variant" --key "$variant" --weight 0 >/dev/null
  option=$(fs feature variant list "$feature" --jq ".[] | select(.key == \"$variant\") | .id")

  # Self-hosted only: events need somewhere to go. Flagsmith's own database is enough.
  if fs api "$BASE/warehouse-connections/" --jq '.results | length' 2>/dev/null | grep -qx 0; then
    printf '{"warehouse_type":"postgres","name":"Flagsmith database","config":{"source":"flagsmith_database"}}' |
      fs api "$BASE/warehouse-connections/" -X POST --input - >/dev/null
  fi

  metric=$(fs api "$BASE/experiment-metrics/" --jq "[.results[] | select(.definition.event == \"$event\")][0].id // empty")
  if [ -z "$metric" ]; then
    metric=$(printf '{"name":"%s","aggregation":"occurrence","direction":"up","definition":{"version":1,"event":"%s"}}' "$event" "$event" |
      fs api "$BASE/experiment-metrics/" -X POST --input - --jq .id)
  fi

  feature_id=$(fs feature get "$feature" --jq .id)
  exp=$(jq -n --arg n "$feature" --arg h "$hypothesis" --arg c "$control" --argjson f "$feature_id" --argjson o "$option" \
    '{name:$n, feature:$f, hypothesis:$h, experiment_rollout:{enabled:true, rollout_percentage:100,
      feature_state_value:{type:"string", value:$c},
      multivariate_feature_state_values:[{multivariate_feature_option:$o, percentage_allocation:50}]}}' |
    fs api "$BASE/experiments/" -X POST --input - --jq .id)
  printf '{"metric":%s,"expected_direction":"increase"}' "$metric" | fs api "$BASE/experiments/$exp/metrics/" -X POST --input - >/dev/null
  fs api "$BASE/experiments/$exp/start/" -X POST --input - <<<'{}' >/dev/null
  echo "experiment $exp running: $feature = $control (control) vs $variant, metric '$event'"
  ;;
status)
  id=$(experiment_id "$2"); [ -n "$id" ] || die "no running experiment on $2"
  fs api "$BASE/experiments/$id/" --jq '{name, status, hypothesis}'
  fs api "$BASE/experiments/$id/exposures/" --jq '.' 2>/dev/null | head -c 600; echo
  ;;
results)
  id=$(experiment_id "$2"); [ -n "$id" ] || die "no running experiment on $2"
  fs api "$BASE/experiments/$id/exposures/refresh/" -X POST --input - <<<'{}' >/dev/null 2>&1 || true
  fs api "$BASE/experiments/$id/results/refresh/" -X POST --input - <<<'{}' >/dev/null 2>&1 || true
  fs api "$BASE/experiments/$id/results/" --jq '.' | head -c 2500; echo
  ;;
ship)
  [ $# -eq 3 ] || die "usage: ship <feature> <variant|control>"
  id=$(experiment_id "$2"); [ -n "$id" ] || die "no running experiment on $2"
  if [ "$3" = control ]; then body='{"rollout_decision":"control"}'
  else
    option=$(fs feature variant list "$2" --jq ".[] | select(.key == \"$3\") | .id")
    [ -n "$option" ] || die "no variant with key $3"
    body=$(printf '{"rollout_decision":"variant","rollout_variant":%s}' "$option")
  fi
  confirm "end the $2 experiment and serve '$3' to everyone"
  fs api "$BASE/experiments/$id/complete/" -X POST --input - <<<"$body" --jq '{name, status}'
  ;;
*) sed -n '2,9p' "$0"; exit 1 ;;
esac
