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
  dnf install -y curl dnf-utils || fail ""prerequisites""
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
  apt-get install -y curl gnupg apt-transport-https ca-certificates || fail ""prerequisites""
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

log ""installing elasticsearch, kibana, elastic-agent""
if [ ""$PKGMGR"" = ""dnf"" ]; then
  dnf install -y elasticsearch kibana elastic-agent || fail ""package install""
else
  apt-get install -y elasticsearch kibana elastic-agent || fail ""package install""
fi
mkdir -p /etc/elasticsearch/jvm.options.d
printf ""-Xms%sm\n-Xmx%sm\n"" ""$HEAP_MB"" ""$HEAP_MB"" > /etc/elasticsearch/jvm.options.d/vmcreate.options

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

cat > /etc/kibana/kibana.yml <<KIBANAYML
server.host: 0.0.0.0
server.name: vmcreate-elastic-siem
elasticsearch.username: kibana_system
elasticsearch.password: ""${KIBANA_PW}""
KIBANAYML
chmod 600 /etc/kibana/kibana.yml

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
FLEET_TOKEN=$( /usr/share/elasticsearch/bin/elasticsearch-service-tokens create elastic/fleet-server vmcreate-fleet 2>/dev/null | awk '{print $NF}' )
if [ -n ""${FLEET_TOKEN:-}"" ]; then
  if elastic-agent install --non-interactive \
       --fleet-server-es=https://localhost:9200 \
       --fleet-server-es-ca=/etc/elasticsearch/certs/http_ca.crt \
       --fleet-server-service-token=""${FLEET_TOKEN}"" \
       --fleet-server-port=8220 >/dev/null 2>&1; then
    AGENT_TOKEN=$( /usr/share/elasticsearch/bin/elasticsearch-create-enrollment-token -s agent --auto-register 2>/dev/null | tail -n 1 )
    {
      echo ""Fleet server:      https://${GUEST_IP:-<vm-ip>}:8220""
      [ -n ""${AGENT_TOKEN:-}"" ] && echo ""Agent enrollment:  elastic-agent install --url=https://${GUEST_IP:-<vm-ip>}:8220 --enrollment-token=${AGENT_TOKEN} --insecure""
    } >> /opt/vmcreate-elastic/credentials.txt
    log ""fleet server installed""
  else
    log ""WARNING: fleet server install failed — ES+Kibana are up; see /var/log/vmcreate-elastic.log""
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
            return sb.ToString();
        }

        private static string JsonEscape(string value) =>
            value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
    }
}
