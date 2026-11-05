# ChronoSpatial: 4D Living Semantic Maps for Mixed Reality Navigation

Meta Global AI Hackathon submission. Point a phone at a room, tap SCAN, and
watch it become a persistent, living semantic map: chairs, doors, tables,
couches and stairs detected by Meta SAM 3.1, anchored as 3D wireframes in
world space, reasoned over by Muse Spark, and navigable with
'Guide me to the chair'.

## Stack

- Unity 6000.4 LTS + AR Foundation (ARCore on Android).
- Meta SAM 3.1 (model sam-3.1, Responses API, streaming SSE) for zero-shot
  2D segmentation of captured keyframes - one noun-phrase concept per
  request, box plus mask tokens per detected object.
- Muse Spark 1.3 for topological and spatial reasoning over the live map.
- Built with Muse Code and Unity MCP: agentic development against a
  deterministic regression suite (110+ edit-mode checks).

## How it works

1. Scan: AR frames become accepted keyframes with capture-time poses and
   downscaled intrinsics; tracking, acquisition and mapping health are
   traced per poll.
2. Segment: up to four overlapping SAM lanes cycle
   chair, door, table, couch, stairs and elevators. Masks decode from
   response.output_text.delta; empty output is a documented zero-match.
3. Lift to 3D: ORB landmark depth consensus first, ARCore plane-raycast
   fallback second. Every box carries its depth provenance
   (source=landmark or source=plane) and is oriented on the
   capture-camera basis, so portrait-held phones never transpose boxes.
4. Confirm: first sightings render immediately in provisional grey;
   a second agreeing sighting flips the box to its status color.
5. Navigate: save and load persistent maps, visual re-localization,
   entry markers, route arrows, Muse Spark guidance.
6. Honesty rails: a STARVED tripwire when the camera delivers no frames,
   per-response Decode accounting, a req/sha identity chain from JPEG to
   response, and an EmptyDetail event census on every empty result.

## App controls

SCAN starts an environment scan, STOP finalizes it. The SCAN tab
re-scans, Details opens live telemetry with its own Close button, and the
status chips (tracking, frames, pose, objects) ride full-width above the
dock. Save Map / Load persist the living map.

## Open and build

1. Open this folder with Unity 6000.4 and open
   Assets/Scenes/SpatialAIScene.unity.
2. Set your API key in-app via Settings, Test and Save (stored on the
   device, never committed), or export META_API_KEY / SAM_API_KEY for
   Editor-side tooling.
3. Build to Android and scan a well-lit room: hold the target in frame
   with slow sideways drift so SLAM triangulates depth.

## Verify

In the Editor, run SpatialAI / Run Deterministic Regression Checks.
It exercises the SLAM baselines, the UI shell, the SAM quality gates,
the dispatch overlap policy, the lifter geometry and the depth guards.

## Layout

- Assets/Scripts/ - the entire C# codebase (SLAM, semantics, UI).
- Assets/Resources/SpatialUI/ - UXML/USS interface shell.
- Assets/Scenes/SpatialAIScene.unity - the reference scene.
- Packages/ - UPM manifest and lock file.
- ProjectSettings/ - project configuration.

Prototype built for the hackathon; exercised on-device (Pixel 4a).
