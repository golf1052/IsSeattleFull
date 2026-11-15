import argparse
import concurrent.futures
import datetime
import hashlib
import json
import urllib.parse
import urllib.request
import uuid
import zipfile
from pathlib import Path


NAMES = [
    "Accessory", "Apartment Complex", "Commercial Building", "Condo Complex and Units",
    "Lookup", "Parcel", "Permit", "Real Property Account", "Real Property Appraisal History",
    "Residential Building", "Unit Breakdown", "Vacant Lot", "Value History",
]


def fetch(url, path, refresh):
    downloaded = None
    modified = None
    if refresh or not path.exists():
        temporary = path.with_name(path.name + "." + uuid.uuid4().hex + ".part")
        try:
            with urllib.request.urlopen(url, timeout=180) as response:
                expected = response.headers.get("Content-Length")
                modified = response.headers.get("Last-Modified")
                with temporary.open("xb") as file:
                    while chunk := response.read(1024 * 1024):
                        file.write(chunk)
                if expected and temporary.stat().st_size != int(expected):
                    raise IOError(f"Incomplete download: {path.name}")
            with temporary.open("rb") as file:
                prefix = file.read(256).lstrip().lower()
            if prefix.startswith((b"<!doctype html", b"<html")):
                raise IOError(f"Unexpected HTML response for {path.name}")
            if path.suffix == ".zip":
                with zipfile.ZipFile(temporary) as archive:
                    if archive.testzip() is not None:
                        raise IOError(f"ZIP integrity error: {path.name}")
            temporary.replace(path)
            downloaded = datetime.datetime.now(datetime.timezone.utc).isoformat()
        finally:
            if temporary.exists():
                temporary.unlink()
    with path.open("rb") as file:
        digest = hashlib.file_digest(file, "sha256").hexdigest()
    return {"bytes": path.stat().st_size, "sha256": digest, "downloadedAtUtc": downloaded, "lastModified": modified}


def main():
    parser = argparse.ArgumentParser(description="Download the assessor archives used by the parcel exporter.")
    parser.add_argument("--accept-data-use", action="store_true",
                        help="Acknowledge the county's RCW 42.56.070(9) restriction on commercial use of lists of individuals.")
    parser.add_argument("--extracted-date", required=True, help="YYYY-MM-DD Date Data Extracted shown on the county portal.")
    parser.add_argument("--output-root", default=str(Path("data") / "assessor-bulk"))
    parser.add_argument("--refresh", action="store_true")
    args = parser.parse_args()
    if not args.accept_data_use:
        parser.error("Review https://info.kingcounty.gov/assessor/DataDownload/default.aspx and explicitly pass --accept-data-use.")
    date = datetime.date.fromisoformat(args.extracted_date)
    directory = Path(args.output_root).resolve() / date.isoformat()
    directory.mkdir(parents=True, exist_ok=True)
    print("Acknowledgment: do not release/use lists of individuals for commercial purposes prohibited by RCW 42.56.070(9).")

    def download(name):
        archive_file = name + ".zip"
        url = "https://aqua.kingcounty.gov/extranet/assessor/" + urllib.parse.quote(archive_file)
        print(f"Downloading/checking {archive_file}", flush=True)
        archive = fetch(url, directory / archive_file, args.refresh)
        if archive["downloadedAtUtc"] is None:
            with zipfile.ZipFile(directory / archive_file) as file:
                if file.testzip() is not None:
                    raise IOError(f"ZIP integrity error: {archive_file}")
        description_file = name + ".doc"
        description_url = "https://info.kingcounty.gov/assessor/datadownload/desc/" + urllib.parse.quote(description_file)
        description = fetch(description_url, directory / description_file, args.refresh)
        return {
            "name": name, "dataExtracted": date.isoformat(),
            "archiveFile": archive_file, "archiveUrl": url, "archive": archive,
            "descriptionFile": description_file, "descriptionUrl": description_url, "description": description,
        }

    with concurrent.futures.ThreadPoolExecutor(max_workers=3) as executor:
        datasets = list(executor.map(download, NAMES))
    manifest_path = directory / "manifest.json"
    if not args.refresh and manifest_path.exists():
        previous = json.loads(manifest_path.read_text(encoding="utf-8"))
        old = {dataset["name"]: dataset for dataset in previous["datasets"]}
        if previous["dataExtracted"] == date.isoformat() and len(old) == len(datasets) and all(
            dataset["name"] in old and
            dataset["archive"]["sha256"] == old[dataset["name"]]["archive"]["sha256"] and
            dataset["description"]["sha256"] == old[dataset["name"]]["description"]["sha256"]
            for dataset in datasets
        ):
            print(f"Reused verified snapshot and unchanged manifest in {directory}")
            return
    manifest = {
        "dataExtracted": date.isoformat(),
        "extractionDateSource": "User-confirmed Date Data Extracted from county portal",
        "generatedAtUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "portal": "https://info.kingcounty.gov/assessor/DataDownload/default.aspx",
        "dataUseAcknowledged": True,
        "datasets": datasets,
    }
    temporary = directory / ("manifest." + uuid.uuid4().hex + ".part")
    try:
        temporary.write_text(json.dumps(manifest, indent=2), encoding="utf-8")
        temporary.replace(manifest_path)
    finally:
        if temporary.exists():
            temporary.unlink()
    print(f"Downloaded {len(datasets)} archives and descriptions to {directory}")


if __name__ == "__main__":
    main()
