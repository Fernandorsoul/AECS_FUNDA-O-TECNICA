#!/usr/bin/env python3
"""Generate complete project.assets.json for AECS projects, resolving full transitive deps."""
import json
import os
import xml.etree.ElementTree as ET
from collections import OrderedDict

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
NUGET_CACHE = os.path.expanduser("~/.nuget/packages")
SDK_PATH = "C:\\Program Files\\dotnet\\sdk\\10.0.400-preview.0.26322.102"

def normalize_version(v):
    """Extract clean version from range like '[5.0.0, 5.0.0]' or '5.0.0'."""
    v = v.strip().strip("[]()")
    if "," in v:
        v = v.split(",")[0].strip()
    return v

def read_nuspec(pkg_id, version):
    """Read nuspec and return dependencies dict for net-compatible TFMs."""
    nuspec = os.path.join(NUGET_CACHE, pkg_id.lower(), version, f"{pkg_id.lower()}.nuspec")
    if not os.path.isfile(nuspec):
        return {}
    try:
        tree = ET.parse(nuspec)
    except ET.ParseError:
        return {}
    root = tree.getroot()
    ns_uri = ""
    if root.tag.startswith("{"):
        ns_uri = root.tag[1:root.tag.index("}")]
    ns = {"n": ns_uri} if ns_uri else {}
    
    deps = {}
    # Prefer TFM-specific groups; fall back to ungrouped deps
    if ns:
        groups = root.findall(".//n:dependencies/n:group", ns)
        best_tfm = None
        best_deps = {}
        for g in groups:
            tfm = (g.get("targetFramework") or "").lower()
            gdeps = {}
            for dep in g.findall("n:dependency", ns):
                did = dep.get("id")
                dver = normalize_version(dep.get("version", "0.0.0"))
                gdeps[did] = dver
            # Prefer net8.0/net9.0/net10.0 > netstandard2.1 > netstandard2.0 > others
            score = 0
            if "net10" in tfm or "net9" in tfm or "net8" in tfm:
                score = 4
            elif "net7" in tfm or "net6" in tfm or "net5" in tfm:
                score = 3
            elif "netstandard2.1" in tfm:
                score = 2
            elif "netstandard2.0" in tfm:
                score = 1
            elif tfm == "":
                score = 0
            if score > best_tfm if best_tfm is not None else True:
                best_tfm = score
                best_deps = gdeps
        deps = best_deps
        if not deps:
            for dep in root.findall(".//n:dependencies/n:dependency", ns):
                deps[dep.get("id")] = normalize_version(dep.get("version", "0.0.0"))
    return deps

def find_libs(pkg_id, version):
    """Find compile/runtime entries from lib/ folder."""
    pkg_path = os.path.join(NUGET_CACHE, pkg_id.lower(), version)
    lib_path = os.path.join(pkg_path, "lib")
    compile_files = {}
    runtime_files = {}
    if os.path.isdir(lib_path):
        # Pick best TFM: net10.0 > net9.0 > net8.0 > netstandard2.1 > netstandard2.0
        tfm_priority = ["net10.0", "net9.0", "net8.0", "net7.0", "net6.0", "netstandard2.1", "netstandard2.0", "netstandard1.0", "net45"]
        available = [d for d in os.listdir(lib_path) if os.path.isdir(os.path.join(lib_path, d))]
        chosen = None
        for pref in tfm_priority:
            if pref in available:
                chosen = pref
                break
        if chosen is None and available:
            chosen = available[0]
        if chosen:
            tfm_dir = os.path.join(lib_path, chosen)
            for f in os.listdir(tfm_dir):
                if f.endswith(".dll") or f.endswith(".xml"):
                    rel = f"lib/{chosen}/{f}"
                    if f.endswith(".dll"):
                        compile_files[rel] = {}
                        runtime_files[rel] = {}
    # Also check ref/ folder
    ref_path = os.path.join(pkg_path, "ref")
    if os.path.isdir(ref_path):
        tfm_priority = ["net10.0", "net9.0", "net8.0", "net7.0", "net6.0", "netstandard2.1", "netstandard2.0"]
        available = [d for d in os.listdir(ref_path) if os.path.isdir(os.path.join(ref_path, d))]
        chosen = None
        for pref in tfm_priority:
            if pref in available:
                chosen = pref
                break
        if chosen:
            tfm_dir = os.path.join(ref_path, chosen)
            for f in os.listdir(tfm_dir):
                if f.endswith(".dll"):
                    rel = f"ref/{chosen}/{f}"
                    compile_files[rel] = {}
    # Check build/ folder for props/targets
    build_files = {}
    build_path = os.path.join(pkg_path, "build")
    if os.path.isdir(build_path):
        for f in os.listdir(build_path):
            if f.endswith(".props") or f.endswith(".targets"):
                build_files[f"build/{f}"] = {}
    # Check buildMultiTargeting/
    build_mt_path = os.path.join(pkg_path, "buildMultiTargeting")
    if os.path.isdir(build_mt_path):
        for f in os.listdir(build_mt_path):
            if f.endswith(".props") or f.endswith(".targets"):
                build_files[f"buildMultiTargeting/{f}"] = {}
    return compile_files, runtime_files, build_files

def version_tuple(v):
    """Parse version string to comparable tuple."""
    parts = []
    for p in v.split("."):
        try:
            parts.append(int(p))
        except ValueError:
            # Handle prerelease like "5.0.0-rc.1"
            num = ""
            for c in p:
                if c.isdigit():
                    num += c
                else:
                    break
            parts.append(int(num) if num else 0)
    return tuple(parts)

def resolve_all_packages(top_packages):
    """Resolve all packages with version conflict resolution (highest wins)."""
    # Map of pkg_id -> (version, deps)
    chosen = {}
    
    def consider(pkg_id, version):
        # Normalize to available version in cache
        pkg_path = os.path.join(NUGET_CACHE, pkg_id.lower(), version)
        if not os.path.isdir(pkg_path):
            pkg_dir = os.path.join(NUGET_CACHE, pkg_id.lower())
            if os.path.isdir(pkg_dir):
                versions = sorted(os.listdir(pkg_dir), key=version_tuple, reverse=True)
                if versions:
                    version = versions[0]
                else:
                    return
            else:
                return
        
        if pkg_id in chosen:
            existing_ver = chosen[pkg_id][0]
            if version_tuple(version) > version_tuple(existing_ver):
                deps = read_nuspec(pkg_id, version)
                chosen[pkg_id] = (version, deps)
                for d_id, d_ver in deps.items():
                    consider(d_id, d_ver)
            # If existing is higher or equal, skip
        else:
            deps = read_nuspec(pkg_id, version)
            chosen[pkg_id] = (version, deps)
            for d_id, d_ver in deps.items():
                consider(d_id, d_ver)
    
    for pkg_id, version in top_packages.items():
        consider(pkg_id, version)
    
    # Build resolved dict
    resolved = {}
    for pkg_id, (version, deps) in chosen.items():
        key = f"{pkg_id}/{version}"
        compile_files, runtime_files, build_files = find_libs(pkg_id, version)
        entry = {
            "type": "package",
            "dependencies": deps,
            "compile": compile_files,
            "runtime": runtime_files,
        }
        if build_files:
            entry["build"] = build_files
        resolved[key] = entry
    
    return resolved

def make_assets(project_rel, top_packages, project_refs):
    """Create project.assets.json for a project."""
    project_path = os.path.join(REPO, project_rel)
    project_dir = os.path.dirname(project_path)
    project_name = os.path.splitext(os.path.basename(project_rel))[0]
    rel_project = project_rel.replace("/", "\\")
    rel_obj = os.path.join(project_dir, "obj").replace("/", "\\")
    rel_obj = os.path.relpath(os.path.join(project_dir, "obj"), REPO).replace("/", "\\")

    resolved = resolve_all_packages(top_packages)

    proj_ref_dict = {}
    for ref in project_refs:
        ref_rel = ref.replace("/", "\\")
        proj_ref_dict[ref_rel] = {"projectPath": ref_rel}

    pkg_deps_list = [f"{k} >= {v}" for k, v in top_packages.items()]

    libraries = {}
    for key, val in resolved.items():
        pkg_id, version = key.split("/")
        libraries[key] = {
            "sha512": "",
            "type": "package",
            "path": f"{pkg_id.lower()}/{version}",
            "files": list(val.get("compile", {}).keys()),
        }

    return {
        "version": 3,
        "targets": {"net10.0": resolved},
        "libraries": libraries,
        "projectFileDependencyGroups": {"net10.0": pkg_deps_list},
        "packageFolders": {NUGET_CACHE + "\\": {}},
        "project": {
            "version": "1.0.0",
            "restore": {
                "projectUniqueName": rel_project,
                "projectName": project_name,
                "projectPath": rel_project,
                "packagesPath": NUGET_CACHE + "\\",
                "outputPath": rel_obj + "\\",
                "projectStyle": "PackageReference",
                "originalTargetFrameworks": ["net10.0"],
                "sources": {"https://api.nuget.org/v3/index.json": {}},
                "frameworks": {"net10.0": {"projectReferences": proj_ref_dict}},
            },
            "frameworks": {
                "net10.0": {
                    "targetAlias": "net10.0",
                    "imports": ["net461", "net462", "net47", "net471", "net472", "net48", "net481"],
                    "assetTargetFallback": True,
                    "warn": True,
                    "downloadDependencies": [],
                    "frameworkReferences": {"Microsoft.NETCore.App": {"privateAssets": "all"}},
                    "runtimeIdentifierGraphPath": SDK_PATH + "\\PortableRuntimeIdentifierGraph.json",
                }
            },
        },
    }

# Project definitions
projects = {
    "src/AECS.Domain/AECS.Domain.csproj": {
        "packages": {},
        "refs": [],
    },
    "src/AECS.Application/AECS.Application.csproj": {
        "packages": {
            "Microsoft.Build.Locator": "1.7.8",
            "Microsoft.CodeAnalysis.CSharp.Workspaces": "5.0.0",
            "Microsoft.CodeAnalysis.Workspaces.MSBuild": "5.0.0",
            "YamlDotNet": "18.1.0",
        },
        "refs": ["src/AECS.Domain/AECS.Domain.csproj"],
    },
    "src/AECS.Infrastructure/AECS.Infrastructure.csproj": {
        "packages": {
            "Microsoft.EntityFrameworkCore.Design": "10.0.4",
            "Npgsql.EntityFrameworkCore.PostgreSQL": "10.0.3",
        },
        "refs": ["src/AECS.Domain/AECS.Domain.csproj"],
    },
    "src/AECS.Cli/AECS.Cli.csproj": {
        "packages": {},
        "refs": [
            "src/AECS.Application/AECS.Application.csproj",
            "src/AECS.Infrastructure/AECS.Infrastructure.csproj",
            "src/AECS.Domain/AECS.Domain.csproj",
        ],
    },
    "tests/AECS.UnitTests/AECS.UnitTests.csproj": {
        "packages": {
            "coverlet.collector": "6.0.4",
            "FluentAssertions": "8.10.0",
            "Microsoft.NET.Test.Sdk": "17.14.1",
            "xunit": "2.9.3",
            "xunit.runner.visualstudio": "3.1.4",
            "YamlDotNet": "18.1.0",
        },
        "refs": [
            "src/AECS.Domain/AECS.Domain.csproj",
            "src/AECS.Application/AECS.Application.csproj",
            "src/AECS.Infrastructure/AECS.Infrastructure.csproj",
            "src/AECS.Cli/AECS.Cli.csproj",
        ],
    },
}

def collect_transitive_packages(project_rel, projects, visited=None):
    """Collect all packages from a project and its transitive project references."""
    if visited is None:
        visited = set()
    if project_rel in visited:
        return {}
    visited.add(project_rel)
    
    config = projects.get(project_rel, {"packages": {}, "refs": []})
    all_packages = dict(config["packages"])
    
    for ref in config["refs"]:
        ref_packages = collect_transitive_packages(ref, projects, visited)
        for pkg_id, version in ref_packages.items():
            if pkg_id not in all_packages:
                all_packages[pkg_id] = version
            else:
                # Keep highest version
                if version_tuple(version) > version_tuple(all_packages[pkg_id]):
                    all_packages[pkg_id] = version
    
    return all_packages

for proj_rel, config in projects.items():
    proj_path = os.path.join(REPO, proj_rel)
    obj_dir = os.path.join(os.path.dirname(proj_path), "obj")
    os.makedirs(obj_dir, exist_ok=True)
    # Collect transitive packages from project references
    all_packages = collect_transitive_packages(proj_rel, projects)
    assets = make_assets(proj_rel, all_packages, config["refs"])
    assets_path = os.path.join(obj_dir, "project.assets.json")
    with open(assets_path, "w", encoding="utf-8") as f:
        json.dump(assets, f, indent=2)
    n_targets = len(assets["targets"]["net10.0"])
    print(f"  {proj_rel}: {n_targets} packages resolved -> {assets_path}")
    cache_path = os.path.join(obj_dir, "project.nuget.cache")
    with open(cache_path, "w", encoding="utf-8") as f:
        json.dump({"version": 2, "dgSpecHash": "manual", "success": True}, f)

print("\nDone!")
