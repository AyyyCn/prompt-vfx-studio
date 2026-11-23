# Prompt VFX Studio

Describe a visual effect in the Unity Editor. Prompt VFX Studio asks OpenAI for original C# and shader source, builds a prefab in a temporary Unity project, then sends four Play Mode frames back for a visual revision. You can compare both drafts and import either into your scene.

![A rose-colored spiral nebula with two dusty arms and a bright core, rendered in Unity](Documentation~/rosewater.jpg)

*Rosewater spiral nebula, captured in Unity from an earlier version of the direct-authoring workflow.*

## Install

Use a **Unity 6.3 Universal Render Pipeline (URP)** project. The editor needs a graphics device, room for a second Unity project and its import cache, and internet access. Direct authoring uses your own OpenAI API key. API charges go to its project.

1. Open **Window → Package Management → Package Manager**.
2. Open the **+** menu and select **Install package from git URL**.
3. Paste the versioned URL and click **Install**.

```text
https://github.com/AyyyCn/prompt-vfx-studio.git#v0.5.2
```

For an installation without Git, download the [package tarball](https://github.com/AyyyCn/prompt-vfx-studio/releases/download/v0.5.2/com.arcade.vfx-author-0.5.2.tgz). Choose **Install package from tarball** and select the downloaded file.

## Make your first effect

1. Open **Window → VFX → Prompt VFX Studio**. Paste your OpenAI API key into the masked field. Alternatively, launch the editor with `OPENAI_API_KEY` set. A pasted key stays in editor memory.
2. Describe what the effect looks like and how it moves. **Try an example prompt** fills the brief with an editable jellyfish example.
3. Click **Generate effect**. Unity starts a second editor, compiles the generated code and captures four moments from the animation. The model can repair compiler errors and revise what it sees.
4. Compare **First draft** and **Visual revision**. Click **Import first draft and select prefab** or **Import visual revision and select prefab**. If assets from this job already exist, Unity asks before replacing them.
5. Unity highlights `Effect.prefab` in the Project window. **Select existing prefab in Project** highlights it again. Drag it into your scene and enter Play Mode. Its runtime scripts, materials and shaders travel with it. Playback works offline.

## What runs and what gets sent

The main window calls the OpenAI Responses API directly. It sends the prompt, generated source, relevant Unity diagnostics and preview images. The default model is `gpt-5.4`. **Model settings** accepts another model with image input and structured output. A job requests a first draft and one visual revision, with up to two repairs per draft.

Generated C# runs **before import**, in a separate editor process with your operating-system permissions. Use a machine or environment where you are comfortable running generated code. The job’s editable source project lives in your temporary directory.

## If generation stops

- **Missing key or HTTP 401.** Enter a valid API key in the editor window. For HTTP 429, check the API project's credits and rate limits.
- **Compile or render error.** Click **Show job files** to inspect the Unity log for that attempt. A completed first draft stays available if the revision fails or you cancel it.
- **Disk space.** Previews, exports and logs are under `Library/PromptVfxJobs` in the working project. Each job's `scratch.txt` identifies its temporary Unity project. Once the job has ended and you have kept the assets you want, you can remove that job’s temporary project to reclaim its build cache.

## Optional surface editor

**Window → VFX → Surface VFX Authoring** opens the older layer-based shader workflow. This optional window uses a signed-in Claude Code installation. Package Manager also offers the original particle samples. The main **Prompt VFX Studio** window uses the API key workflow above.
