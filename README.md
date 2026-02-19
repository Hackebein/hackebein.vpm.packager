# Hackebein's VPM Packager (Unity Editor)

Unity Editor tooling to **edit VRChat VPM `package.json` files**, **build/export VPM zip packages**, and **publish releases straight from Unity** (including direct uploads to **VPMM**).

Package ID: `hackebein.vpm.packager` · Unity: `2022.3+`

Add `https://vpm.hackebein.dev/index.json` or `https://vpmm.dev/hackebein.vpm.packager.json` to VCC/ALCOM.

## What it does

- **`package.json` editor** inside Unity (no more hand-editing JSON in an external editor)
- **One-click VPM export**: builds a proper VPM zip from your Unity content
- **Publish flow from Unity**: prepare a release + upload artifacts to your **VPMM** instance so you can ship updates without leaving the editor
- **Creator-friendly workflow**: keeps "release & publish" close to where you actually work (Unity)

## Why this exists

Shipping VPM packages often turns into a context-switch marathon (edit JSON → build zip → tag/release → upload → update listing).  
This tool aims to make it a **single, repeatable Unity-native flow**: *edit → build → publish*.

## Requirements

- Unity 2022.3
- [VPMM.dev API Key](https://vpmm.dev/) (top right)
