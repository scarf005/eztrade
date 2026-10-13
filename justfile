export RIMWORLD_DIR := env("RIMWORLD_DIR", "/media/scarf/@steam/SteamLibrary/steamapps/common/RimWorld")
export RIMWORLD_MODS_CONFIG := env("RIMWORLD_MODS_CONFIG", "/home/scarf/.config/unity3d/Ludeon Studios/RimWorld by Ludeon Studios/Config/ModsConfig.xml")

mod_directory := "EzTrade"

# Format F# with Fantomas.
fmt:
    mise exec dotnet@10.0.301 -- dotnet tool restore
    mise exec dotnet@10.0.301 -- dotnet tool run fantomas Source/EzTrade

legacy_ref := env("RIMWORLD_1_6_REF", "rimworld-1.6")
legacy_dir := "Source/obj/rimworld-1.6"

# Build the RimWorld 1.7 and frozen 1.6 assemblies.
build: build-17 build-16

# Build the RimWorld 1.7 assembly.
build-17:
    mise exec dotnet@10.0.301 -- dotnet build Source/EzTrade/EzTrade.fsproj -c Release

# Build the RimWorld 1.6 assembly from the frozen 1.6 branch.
build-16:
    mkdir -p {{legacy_dir}}
    git archive {{legacy_ref}} Source | tar -x -C {{legacy_dir}}
    mise exec dotnet@10.0.301 -- dotnet build {{legacy_dir}}/Source/EzTrade/EzTrade.fsproj -c Release -p:OutputPath="$PWD/1.6/Assemblies/"

# Build and install the runtime mod locally.
install: build
    #!/bin/sh
    set -eu
    mods_directory="${RIMWORLD_DIR}/Mods"
    if [ ! -d "$mods_directory" ]; then
        printf 'RimWorld Mods directory not found: %s\n' "$mods_directory" >&2
        exit 1
    fi
    destination="$mods_directory/{{mod_directory}}"
    mkdir -p "$destination"
    rsync --archive --delete --delete-excluded \
        --exclude '/.*' \
        --exclude '/Source/' \
        --exclude '/README*' \
        --exclude '/LICENSE*' \
        --exclude '/justfile' \
        ./ "$destination/"
    printf 'Installed {{mod_directory}} to %s\n' "$destination"

# Build, install, and enable the mod locally.
install-enable: install
    #!/usr/bin/env python3
    import os
    import subprocess
    import xml.etree.ElementTree as ET
    from pathlib import Path

    if subprocess.run(
        ["pgrep", "-x", "RimWorldLinux|RimWorld"],
        check=False,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
    ).returncode == 0:
        raise SystemExit("Close RimWorld before enabling the mod.")

    package_id = (ET.parse("About/About.xml").getroot().findtext("packageId") or "").strip()
    if not package_id:
        raise SystemExit("About/About.xml has no packageId.")

    config_path = Path(os.environ["RIMWORLD_MODS_CONFIG"])
    tree = ET.parse(config_path)
    active_mods = tree.getroot().find("activeMods")
    if active_mods is None:
        raise SystemExit(f"{config_path} has no activeMods element.")

    if package_id in ((node.text or "").strip() for node in active_mods.findall("li")):
        print(f"{package_id} is already enabled.")
        raise SystemExit(0)

    ET.SubElement(active_mods, "li").text = package_id
    ET.indent(tree, space="  ")
    temporary_path = config_path.with_name(f"{config_path.name}.tmp")
    tree.write(temporary_path, encoding="utf-8", xml_declaration=True)
    os.chmod(temporary_path, config_path.stat().st_mode)
    temporary_path.replace(config_path)
    print(f"Enabled {package_id} in {config_path}.")
