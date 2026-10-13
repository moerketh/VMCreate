using Microsoft.Extensions.Logging;
using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate
{
    /// <summary>
    /// Shared engine for the Elastic SIEM steps: writes an idempotent setup
    /// script into the guest (base64 over SSH to avoid quoting issues) and
    /// runs it. Used by both the appliance step (Elastic SIEM gallery item,
    /// always executed) and the add-on step (optional card on Debian-family
    /// distributions).
    /// <para>
    /// Stands up a single-node lab stack from the official Elastic 9.x
    /// repositories — dnf (EL family: Rocky/Alma) or apt (Debian family),
    /// selected at runtime in the guest: Elasticsearch (auto-configured TLS,
    /// heap sized to the VM), Kibana (bound to 0.0.0.0), and an Elastic Agent
    /// Fleet server. Secrets (elastic/kibana_system passwords, service token,
    /// enrollment token) are generated inside the guest and written to
    /// <c>/opt/vmcreate-elastic/credentials.txt</c> (mode 600) — they never
    /// cross back to the host logs. AI enablement, when requested, creates
    /// the <c>_inference/chat_completion</c> endpoint from the stored LLM
    /// config.
    /// </para>
    /// <para>
    /// <see cref="BuildScript"/> is internal for testability: the tests
    /// assert on the generated bash (package-manager branches, secret-free
    /// stdout, AI endpoint wiring).
    /// </para>
    /// </summary>
    internal static class ElasticStackInstaller
    {
        /// <summary>Visible to tests: generates the guest setup script.</summary>
        internal static string BuildScriptForTest(bool aiEnabled, bool startTrial, string aiUrl, string aiKey, string aiModel)
            => BuildScript(aiEnabled, startTrial, aiUrl, aiKey, aiModel);
        public const string StepName = "Install Elastic SIEM";

        public static async Task ExecuteAsync(
            IGuestShell shell, GalleryItem item, VmCustomizations customizations,
            ILogger logger, CancellationToken ct)
        {
            var ai = customizations.ElasticAi;
            bool aiEnabled = ai.IsComplete;
            if (ai.Enabled && !aiEnabled)
                logger.LogWarning("Elastic AI requested but LLM configuration is incomplete — skipping AI enablement");

            logger.LogInformation("Installing the Elastic Stack on VM {VMName} (AI: {Ai}, trial license: {Trial})",
                shell.VmName, aiEnabled ? "on" : "off", aiEnabled && ai.StartTrialLicense ? "yes" : "no");

            // The script itself never echoes secrets to stdout: phase markers
            // only. Credentials live in /opt/vmcreate-elastic/credentials.txt.
            var script = BuildScript(
                aiEnabled,
                ai.StartTrialLicense,
                ai.ChatCompletionsUrl ?? string.Empty,
                ai.ApiKey ?? string.Empty,
                ai.Model ?? string.Empty);

            // Base64 transport: the LLM API key is part of the payload when AI
            // is enabled, so the script must not be visible in process listings.
            string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(script));
            logger.LogInformation("Deploying Elastic setup script to VM {VMName} ({Bytes} bytes)", shell.VmName, script.Length);

            await shell.RunCommandAsync(
                $"echo {b64} | base64 -d | sudo tee /opt/vmcreate-elastic-setup.sh >/dev/null && sudo chmod 700 /opt/vmcreate-elastic-setup.sh",
                ct);

            string output = await shell.RunCommandAsync(
                "sudo bash /opt/vmcreate-elastic-setup.sh 2>&1; rc=$?; sudo rm -f /opt/vmcreate-elastic-setup.sh; exit $rc",
                TimeSpan.FromMinutes(25), ct);

            logger.LogInformation("Elastic Stack setup output for VM {VMName}:\n{Output}", shell.VmName, output);

            if (output.Contains("ELASTIC_SETUP_FAILED", StringComparison.Ordinal))
                throw new Exception("Elastic Stack setup failed — see the deployment log and /var/log/vmcreate-elastic.log on the guest.");
        }

        private static string BuildScript(bool aiEnabled, bool startTrial, string aiUrl, string aiKey, string aiModel)
        {
            var sb = new StringBuilder();
            sb.AppendLine(@"#!/usr/bin/env bash
# VMCreate Elastic SIEM setup — idempotent. Secrets stay in the guest.
set -uo pipefail
exec >>/var/log/vmcreate-elastic.log 2>&1
fail() { echo ""ELASTIC_SETUP_FAILED: $*""; echo ""ELASTIC_SETUP_FAILED: $*"" >&2; exit 1; }
log()  { echo ""[vmcreate-elastic] $*""; echo ""[vmcreate-elastic] $*"" >&2; }

if systemctl is-active --quiet elasticsearch.service && [ -f /opt/vmcreate-elastic/.installed ]; then
  log ""Elastic Stack already installed — skipping""
  echo ""ELASTIC_SETUP_OK already-installed""
  exit 0
fi

[ ""$(id -u)"" -eq 0 ] || fail ""must run as root""

# ── Package-manager branch: dnf (EL family: Rocky/Alma) or apt (Debian family) ──
PKGMGR=unknown
if command -v dnf >/dev/null 2>&1; then
  PKGMGR=dnf
elif command -v apt-get >/dev/null 2>&1; then
  PKGMGR=apt
else
  fail ""neither dnf nor apt-get found (EL or Debian family required)""
fi
log ""package manager: ${PKGMGR}""

if [ ""$PKGMGR"" = ""dnf"" ]; then
  log ""installing prerequisites and Elastic rpm repository""
  dnf install -y curl dnf-utils openssl || fail ""prerequisites""
  rpm --import https://artifacts.elastic.co/GPG-KEY-elasticsearch || fail ""GPG key""
  cat > /etc/yum.repos.d/elastic-9.x.repo <<'ELREPO'
[elastic-9.x]
name=Elastic repository for 9.x packages
baseurl=https://artifacts.elastic.co/packages/9.x/yum
gpgcheck=1
gpgkey=https://artifacts.elastic.co/GPG-KEY-elasticsearch
enabled=1
autorefresh=1
type=rpm-md
ELREPO
  dnf makecache --disablerepo='*' --enablerepo='elastic-9.x' || fail ""dnf makecache (elastic repo)""
else
  log ""installing prerequisites and Elastic apt repository""
  export DEBIAN_FRONTEND=noninteractive
  apt-get update -y || fail ""apt-get update""
  apt-get install -y curl gnupg apt-transport-https ca-certificates openssl || fail ""prerequisites""
  curl -fsSL https://artifacts.elastic.co/GPG-KEY-elasticsearch | gpg --dearmor --yes -o /usr/share/keyrings/elasticsearch-keyring.gpg || fail ""GPG key""
  echo ""deb [signed-by=/usr/share/keyrings/elasticsearch-keyring.gpg] https://artifacts.elastic.co/packages/9.x/apt stable main"" > /etc/apt/sources.list.d/elastic-9.x.list
  apt-get update -y || fail ""apt-get update (elastic repo)""
fi

log ""sizing JVM heap for a single-node lab (Elasticsearch + Kibana on one host)""
TOTAL_MB=$(( $(grep MemTotal /proc/meminfo | awk '{print $2}') / 1024 ))
HEAP_MB=$(( TOTAL_MB / 2 - 2048 ))
[ ""$HEAP_MB"" -lt 1024 ] && HEAP_MB=1024
[ ""$HEAP_MB"" -gt 30720 ] && HEAP_MB=30720
log ""total RAM ${TOTAL_MB} MB -> heap ${HEAP_MB} MB""

# Elastic 9.x installs the basic flavor by default, which omits the
# fleet-server binary (breaking change elastic/elastic-agent#6542). The
# rpm/dpkg maintainer scripts read this variable and stamp the flavor on
# install. Without it, elastic-agent can never start a fleet server.
export ELASTIC_AGENT_FLAVOR=servers
log ""installing elasticsearch, kibana, elastic-agent""
if [ ""$PKGMGR"" = ""dnf"" ]; then
  dnf install -y elasticsearch kibana elastic-agent || fail ""package install""
else
  apt-get install -y elasticsearch kibana elastic-agent || fail ""package install""
fi
mkdir -p /etc/elasticsearch/jvm.options.d
printf '%s\n' ""-Xms${HEAP_MB}m"" ""-Xmx${HEAP_MB}m"" > /etc/elasticsearch/jvm.options.d/vmcreate.options

systemctl daemon-reload
systemctl enable elasticsearch.service kibana.service >/dev/null 2>&1
systemctl restart elasticsearch.service || fail ""start elasticsearch""

log ""waiting for Elasticsearch to answer""
ES_OK=0
for i in $(seq 1 60); do
  if curl -ks --max-time 5 https://localhost:9200 >/dev/null 2>&1; then ES_OK=1; break; fi
  sleep 5
done
[ ""$ES_OK"" -eq 1 ] || fail ""elasticsearch did not become ready""
log ""elasticsearch is up""

log ""generating credentials (kept inside the guest)""
ES_PW=$(/usr/share/elasticsearch/bin/elasticsearch-reset-password -u elastic -b -s 2>/dev/null) || fail ""reset elastic password""
KIBANA_PW=$(/usr/share/elasticsearch/bin/elasticsearch-reset-password -u kibana_system -b -s 2>/dev/null) || fail ""reset kibana_system password""

# Kibana Fleet refuses to initialise without xpack encryption keys (every
# fleet API stays uninitialised — live-observed on the Rocky E2E rig).
# Random per install; 32-hex-char keys are the standard single-node pattern.
ENC_KEY=$(openssl rand -hex 16)
SEC_KEY=$(openssl rand -hex 16)
REP_KEY=$(openssl rand -hex 16)
cat > /etc/kibana/kibana.yml <<KIBANAYML
server.host: 0.0.0.0
server.name: vmcreate-elastic-siem
elasticsearch.hosts: [""https://localhost:9200""]
elasticsearch.username: kibana_system
elasticsearch.password: ""${KIBANA_PW}""
elasticsearch.ssl.certificateAuthorities: [""/etc/kibana/http_ca.crt""]
xpack.encryptedSavedObjects.encryptionKey: ""${ENC_KEY}${SEC_KEY}""
xpack.security.encryptionKey: ""${SEC_KEY}${REP_KEY}""
xpack.reporting.encryptionKey: ""${REP_KEY}${ENC_KEY}""
xpack.fleet.agents.enabled: true
KIBANAYML
# The RPM/deb default is root:kibana 0660 (cf. kibana.keystore); chmod 600
# root:root made the kibana user unable to read the config (EACCES FATAL,
# observed live) and dropped the TLS CA path the 9.x http stack requires.
chown root:kibana /etc/kibana/kibana.yml
chmod 0660 /etc/kibana/kibana.yml
# /etc/elasticsearch/certs is root:elasticsearch 0750 — kibana cannot
# traverse it, so the http CA must be copied to a kibana-readable spot.
install -o root -g kibana -m 0640 /etc/elasticsearch/certs/http_ca.crt /etc/kibana/http_ca.crt

systemctl restart kibana.service || fail ""start kibana""
log ""waiting for Kibana to answer""
KIB_OK=0
for i in $(seq 1 60); do
  if curl -s --max-time 5 -o /dev/null http://localhost:5601/api/status; then KIB_OK=1; break; fi
  sleep 5
done
[ ""$KIB_OK"" -eq 1 ] || log ""WARNING: kibana not answering yet on 5601 (continuing)""

install -d -m 700 /opt/vmcreate-elastic
umask 077
GUEST_IP=$(ip -4 route get 1.1.1.1 2>/dev/null | awk '{print $7; exit}')
{
  echo ""Elastic SIEM deployed by VMCreate on $(date -u +%FT%TZ)""
  echo ""Kibana:            http://${GUEST_IP:-<vm-ip>}:5601""
  echo ""Elasticsearch:     https://${GUEST_IP:-<vm-ip>}:9200""
  echo ""elastic password:  ${ES_PW}""
  echo ""(also in: sudo cat /opt/vmcreate-elastic/credentials.txt)""
} > /opt/vmcreate-elastic/credentials.txt

log ""installing Elastic Agent as a Fleet server (best-effort)""
# ES 9.x ships /etc/elasticsearch/service_tokens root:root 0600, so every
# service-token auth 401s silently until ES can read the file (observed
# live, defect #2 of the fleet chain). Fix perms BEFORE the first token
# is created, then give the ES file-token watcher time to reload.
touch /etc/elasticsearch/service_tokens
chown root:elasticsearch /etc/elasticsearch/service_tokens
chmod 0660 /etc/elasticsearch/service_tokens
FLEET_TOKEN=$( /usr/share/elasticsearch/bin/elasticsearch-service-tokens create elastic/fleet-server vmcreate-fleet 2>/dev/null | awk '{print $NF}' )
sleep 10
FLEET_POLICY_ID=""""
AGENT_POLICY_ID=""""
AGENT_TOKEN=""""
FLEET_HEALTHY=0
if [ -n ""${FLEET_TOKEN:-}"" ]; then
  # One-time Fleet index initialisation via Kibana (idempotent; needs the
  # xpack keys above). Can take up to ~2 min on a fresh node — observed
  # live that it returns HTTP 200 once initialised.
  FLEET_SETUP_OK=0
  for i in $(seq 1 12); do
    SETUP_CODE=$(curl -s --max-time 30 -o /opt/vmcreate-elastic/fleet-setup.json -w '%{http_code}' -u ""elastic:${ES_PW}"" -H 'kbn-xsrf: true' -X POST http://localhost:5601/api/fleet/setup)
    echo ""fleet setup attempt ${i}: http ${SETUP_CODE}""
    [ ""${SETUP_CODE}"" = ""200"" ] && { FLEET_SETUP_OK=1; break; }
    sleep 10
  done
  if [ ""${FLEET_SETUP_OK}"" -eq 1 ]; then
    # Default fleet-server policy. has_fleet_server must be true or the
    # agent stalls on ""Waiting on default policy"" (live-observed defect #4).
    FLEET_POLICY_RESP=$(curl -s --max-time 30 -u ""elastic:${ES_PW}"" -H 'kbn-xsrf: true' -H 'Content-Type: application/json' -X POST http://localhost:5601/api/fleet/agent_policies --data '{""name"":""VMCreate Fleet Server Policy"",""namespace"":""default"",""is_default_fleet_server"":true,""has_fleet_server"":true}')
    FLEET_POLICY_ID=$(printf '%s' ""${FLEET_POLICY_RESP}"" | sed -n 's/.*""id"": *""\([^""]*\)"".*/\1/p')
    echo ""fleet-server policy response: ${FLEET_POLICY_RESP}""
  else
    log ""WARNING: Kibana fleet setup never returned 200 — skipping fleet enrollment""
  fi
  if [ -n ""${FLEET_POLICY_ID}"" ]; then
    # enroll (not install): the package already provides the systemd unit,
    # enroll writes the fleet-server config and starts it. --force skips the
    # replace-settings prompt. timeout guards a hang on a slow guest.
    if timeout 180 elastic-agent enroll --force \
         --fleet-server-es=https://localhost:9200 \
         --fleet-server-es-ca=/etc/elasticsearch/certs/http_ca.crt \
         --fleet-server-service-token=""${FLEET_TOKEN}"" \
         --fleet-server-port=8220 </dev/null; then
      # /api/fleet/server-info stays 404 even when healthy — the fleet
      # server status endpoint is /api/status (live-observed).
      for i in $(seq 1 18); do
        FLEET_STATUS=$(curl -sk --max-time 5 https://localhost:8220/api/status)
        echo ""fleet status attempt ${i}: ${FLEET_STATUS}""
        case ""${FLEET_STATUS}"" in *HEALTHY*) FLEET_HEALTHY=1; break ;; esac
        sleep 10
      done
      [ ""${FLEET_HEALTHY}"" -eq 1 ] || log ""WARNING: fleet server did not report HEALTHY in time — see /var/log/vmcreate-elastic.log""
    else
      log ""WARNING: elastic-agent enroll failed — ES+Kibana are up; see /var/log/vmcreate-elastic.log""
    fi
  else
    log ""WARNING: could not create the fleet-server agent policy — skipping enroll""
  fi
  if [ ""${FLEET_HEALTHY}"" -eq 1 ]; then
    # Default agent policy + enrollment key for OTHER VMs reporting to this
    # SIEM. The ES 9.x CLI dropped its enrollment-token subcommand, so the
    # Kibana fleet API is the supported way to mint the key (live-verified).
    AGENT_POLICY_RESP=$(curl -s --max-time 30 -u ""elastic:${ES_PW}"" -H 'kbn-xsrf: true' -H 'Content-Type: application/json' -X POST http://localhost:5601/api/fleet/agent_policies --data '{""name"":""VMCreate Agent Policy"",""namespace"":""default"",""is_default"":true}')
    AGENT_POLICY_ID=$(printf '%s' ""${AGENT_POLICY_RESP}"" | sed -n 's/.*""id"": *""\([^""]*\)"".*/\1/p')
    echo ""agent policy response: ${AGENT_POLICY_RESP}""
    if [ -n ""${AGENT_POLICY_ID}"" ]; then
      AGENT_KEY_RESP=$(curl -s --max-time 30 -u ""elastic:${ES_PW}"" -H 'kbn-xsrf: true' -H 'Content-Type: application/json' -X POST http://localhost:5601/api/fleet/enrollment_api_keys --data ""{\""policy_id\"":\""${AGENT_POLICY_ID}\"",\""name\"":\""VMCreate Agent Enrollment\""}"")
      AGENT_TOKEN=$(printf '%s' ""${AGENT_KEY_RESP}"" | sed -n 's/.*""api_key"": *""\([^""]*\)"".*/\1/p')
      echo ""agent enrollment key response: ${AGENT_KEY_RESP}""
    fi
    {
      echo ""Fleet server:      https://${GUEST_IP:-<vm-ip>}:8220""
      [ -n ""${AGENT_TOKEN:-}"" ] && echo ""Agent enrollment:  elastic-agent install --url=https://${GUEST_IP:-<vm-ip>}:8220 --enrollment-token=${AGENT_TOKEN} --insecure""
    } >> /opt/vmcreate-elastic/credentials.txt
    if [ -n ""${AGENT_TOKEN:-}"" ]; then
      log ""fleet server healthy; agent enrollment key created""
    else
      log ""WARNING: agent enrollment key could not be created — see /var/log/vmcreate-elastic.log""
    fi
  else
    log ""WARNING: fleet server not HEALTHY — agent enrollment left out of credentials.txt""
  fi
else
  log ""WARNING: could not create fleet service token — skipping fleet server""
fi");

            if (aiEnabled)
            {
                string trial = startTrial
                    ? @"  log ""starting 30-day Enterprise trial license (AI features need Enterprise)""
  curl -s -X POST -u ""elastic:${ES_PW}"" ""https://localhost:9200/_license/start_trial?acknowledge=true"" -o /opt/vmcreate-elastic/trial-response.json || log ""WARNING: start_trial call failed""
"
                    : "  log \"AI requested without trial license: inference endpoint will be created, but the AI features need an Enterprise license\"\n";

                // JSON-escape the AI values for the inference PUT body.
                sb.AppendLine($@"if true; then
{trial}  log ""creating inference endpoint for AI features""
  cat > /opt/vmcreate-elastic/inference.json <<INFERJSON
{{""service"":""openai"",""service_settings"":{{""api_key"":""{JsonEscape(aiKey)}"",""model_id"":""{JsonEscape(aiModel)}"",""url"":""{JsonEscape(aiUrl)}""}}}}
INFERJSON
  chmod 600 /opt/vmcreate-elastic/inference.json
  HTTP_CODE=$(curl -s -o /opt/vmcreate-elastic/inference-response.json -w ""%{{http_code}}"" \
    -X PUT -u ""elastic:${{ES_PW}}"" -H ""Content-Type: application/json"" \
    --data-binary @/opt/vmcreate-elastic/inference.json \
    ""https://localhost:9200/_inference/chat_completion/vmcreate-siem"")
  rm -f /opt/vmcreate-elastic/inference.json
  if [ ""$HTTP_CODE"" = ""200"" ]; then
    log ""inference endpoint created (HTTP $HTTP_CODE)""
    echo ""AI: inference endpoint vmcreate-siem configured"" >> /opt/vmcreate-elastic/credentials.txt
  else
    log ""WARNING: inference endpoint creation returned HTTP $HTTP_CODE (see inference-response.json in the guest)""
  fi
fi");
            }

            sb.AppendLine(@"
touch /opt/vmcreate-elastic/.installed
log ""done""
echo ""ELASTIC_SETUP_OK""
exit 0");

            // The script is composed with AppendLine (Environment.NewLine =
            // CRLF on Windows) and executes under guest bash, which chokes on
            // CR ('set: pipefail\r: invalid option name' — observed live on
            // the Rocky appliance E2E). Bash must always receive LF, the same
            // contract ScriptResourceLoader enforces for the .sh resources.
            return sb.ToString().Replace("\r\n", "\n");
        }

        private static string JsonEscape(string value) =>
            value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
    }
}
