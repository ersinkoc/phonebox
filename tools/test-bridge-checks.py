"""Run the phone bridge's JVM checks against the production BridgeClip.kt and AgentStream.kt.

Like test-agent-stream.py: uses the Kotlin compiler and dependencies already cached by the Android Gradle build; needs
no Android device and no test framework. Run the Android Gradle build once first so they are cached.
"""
import os
from pathlib import Path
import re
import subprocess
import tempfile
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
NS = {"m": "http://maven.apache.org/POM/4.0.0"}
APP = ROOT / "android/app/src/main/java/net/oxog/phonebox/deskpanel"
# production files that must compile on a plain JVM (no Android types), then the stand-in link and the checks
PRODUCTION = ["BridgeClip.kt", "AgentStream.kt"]
CHECKS = ["AgentLink.kt", "BridgeChecks.kt"]


def main():
    version = re.search(r'kotlin.android"\) version "([^"]+)"', (ROOT / "android/build.gradle.kts").read_text())[1]
    cache = Path(os.environ.get("GRADLE_USER_HOME", Path.home() / ".gradle")) / "caches/modules-2/files-2.1"

    def artifact(group, name, ver, ext="jar"):
        matches = list((cache / group / name / ver).glob(f"*/{name}-{ver}.{ext}"))
        if not matches:
            raise SystemExit(f"Missing cached {group}:{name}:{ver}; run the Android Gradle build first")
        return matches[0]

    compiler = artifact("org.jetbrains.kotlin", "kotlin-compiler-embeddable", version)
    pom = ET.parse(artifact("org.jetbrains.kotlin", "kotlin-compiler-embeddable", version, "pom"))
    dependencies = [artifact(*(dep.findtext("m:" + key, namespaces=NS) for key in ["groupId", "artifactId", "version"]))
                    for dep in pom.findall("m:dependencies/m:dependency", NS)]
    stdlib = artifact("org.jetbrains.kotlin", "kotlin-stdlib", version)
    annotations = sorted((cache / "org.jetbrains/annotations").glob("*/*/annotations-*.jar"))
    if not annotations:
        raise SystemExit("Missing cached JetBrains annotations; run the Android Gradle build first")
    classpath = os.pathsep.join(map(str, [stdlib, annotations[-1]]))
    output = Path(tempfile.mkdtemp(prefix="phonebox-bridge-check-"))
    compiler_cp = os.pathsep.join(map(str, [compiler, *dependencies, annotations[-1]]))
    sources = [APP / f for f in PRODUCTION] + [ROOT / "tools/kotlin-tests" / f for f in CHECKS]
    # a renamed or deleted file must fail the run, not drop its checks without a word
    missing = [str(f.relative_to(ROOT)) for f in sources if not f.is_file()]
    if missing:
        raise SystemExit("ERROR: missing source files: " + ", ".join(missing))
    subprocess.run(["java", "-cp", compiler_cp, "org.jetbrains.kotlin.cli.jvm.K2JVMCompiler", "-no-stdlib", "-no-reflect",
                    "-jvm-target", "17", "-classpath", classpath, "-d", str(output), *map(str, sources)], check=True, timeout=120)
    result = subprocess.run(["java", "-cp", str(output) + os.pathsep + classpath, "net.oxog.phonebox.deskpanel.BridgeChecksKt"], timeout=120)
    if result.returncode != 0:
        raise SystemExit(result.returncode)


if __name__ == "__main__":
    main()
