#!/bin/bash
# =============================================================================
# Grid Shutdown Script — calls each sim's shutdown API
# Flow: backup → countdown notifications → kill python → quit OpenSim
# Container stays alive (sleep infinity) — restart manually when ready
# =============================================================================
# Usage:
#   ./grid_shutdown.sh              # shutdown all sims (default 180s delay)
#   ./grid_shutdown.sh shutdown 300 # shutdown with 300s delay
#   ./grid_shutdown.sh schedule 60  # restart with 60s delay (backup + quit, Docker restarts)
# =============================================================================

API_TOKEN="KOAKrJ3JJ2BduOxjTIEqjzyVpiKGwfO9SUiKmqF_oZyp5oxSBuRyClv7nrS2F1U5"
ACTION="${1:-shutdown}"
DELAY="${2:-180}"
HOST="127.0.0.1"

# RegionName -> port UUID
declare -A REGIONS=(
  ["Grid_Estate_Services"]="2000 f00c2c3e-55a2-4080-a2ee-d3957a010303"
  ["Grid_Welcome"]="2001 3b7d9bcb-f0c0-49de-84db-33d72446f06a"
  ["Little-Creek"]="2002 b0317b94-0214-43a4-ac41-1cc828ff2d65"
  ["Amber"]="2003 b0317b94-0214-43a4-ac41-1cc828ff2d66"
  ["The_Ceiba_Tree"]="2004 2cbd032a-361b-4f70-990b-de4981a80970"
  ["River-Retreat"]="2005 4b1b5744-9ab1-4507-af62-c0d835171e42"
  ["The_Furniture_Vault"]="2006 5175e2fd-7fa3-4910-83f3-b49afd8ae7f3"
  ["Leeloo"]="2007 e17e1839-5733-45fb-b046-de478b87606f"
  ["Little_Girls"]="2008 10449eab-71d4-4666-9d05-3ec6efc7601b"
  ["Blue-Heaven"]="2009 c00afa57-60bb-41f8-af02-fcc82e817539"
  ["Blue"]="2010 7efa77c7-f38d-47b2-9271-d3ed8126f718"
  ["Out_back"]="2011 00347229-ca93-430f-b69f-fc9eef50aa34"
  ["Clavius"]="2012 88d9d725-a3e8-430e-8aea-ea6f3c3a868c"
  ["New-Horizons"]="2013 2ebf634f-c1a0-4aef-a9d7-c8b5fd914838"
  ["Amber_Store2"]="2014 20000000-0000-4000-8000-000000000000"
  ["Residential_Area_01"]="2015 10000000-0000-4000-0000-000000000001"
  ["Residential_Area_02"]="2016 10000000-0000-4000-0000-000000000002"
  ["Crystal-Island"]="2017 c20bd5ef-e724-4462-8f49-152a06f7640a"
  ["Sunflower-Dream"]="2018 2cbd121a-261b-4f70-880b-de4981a80971"
  ["Amber_Store"]="2019 f00c2c3e-55a2-4080-a2ee-d3957a010302"
  ["little_beens"]="2020 01c343a7-fe38-4261-8137-9ecf596e16ac"
  ["I_LOVE_Sandbox"]="2021 10000000-1000-4000-1000-000000000001"
  ["Casino"]="2022 7efa77c7-f38d-47b2-9271-d3ed8126f720"
  ["Grid_Test"]="2023 3b7d9bcb-f0c0-49de-84db-33d72446f06c"
  ["Abody"]="2026 20000000-0000-4000-8000-000000000001"
  ["Grave"]="2027 cb325e3e-0524-4edb-bca0-5e81de406b02"
  ["events"]="2028 bde59b6b-a550-4c45-bba0-79ef90a467e4"
  ["elf"]="2029 f934be6a-5624-45c6-82a8-c3c12a345045"
  ["Hyperport"]="2030 b9540c60-20ac-4ece-8cb9-b538460e8cad"
  ["White_City_Sim"]="2031 6fac4f54-fb5b-41d5-9fc6-3a8eb0ddf471"
  ["Enchanted_Garden"]="2032 6aed4112-fc16-4c60-9541-d169c8be6758"
  ["testing_polygon"]="2035 7fb5b4eb-7f37-4aa7-947b-dea3becdcdae"
)

echo "========================================="
echo "  GRID $(echo $ACTION | tr '[:lower:]' '[:upper:]')"
echo "  Action: $ACTION"
echo "  Delay:  ${DELAY}s"
echo "  Sims:   ${#REGIONS[@]}"
echo "========================================="
echo ""

# Confirm
read -p "Are you sure? (yes/no): " CONFIRM
if [ "$CONFIRM" != "yes" ]; then
  echo "Aborted."
  exit 0
fi

echo ""

SUCCESS=0
FAILED=0

for NAME in "${!REGIONS[@]}"; do
  read -r PORT UUID <<< "${REGIONS[$NAME]}"
  URL="http://${HOST}:${PORT}/tasia-ngc/restart/${UUID}"
  
  HTTP_CODE=$(curl -s -o /dev/null -w '%{http_code}' \
    -X POST "$URL" \
    -H "Authorization: Bearer $API_TOKEN" \
    -H "Content-Type: application/json" \
    -d "{\"action\":\"$ACTION\",\"delay_seconds\":$DELAY}" \
    --connect-timeout 3 --max-time 10 2>/dev/null)

  if [ "$HTTP_CODE" = "200" ]; then
    echo "  ✅ $NAME (port $PORT)"
    ((SUCCESS++))
  else
    echo "  ❌ $NAME (port $PORT) — HTTP $HTTP_CODE"
    ((FAILED++))
  fi
done

echo ""
echo "========================================="
echo "  Sent to $SUCCESS sims, $FAILED failed"
echo "  Viewers will see countdown notification"
echo "  ${DELAY}s until action completes"
echo "========================================="
