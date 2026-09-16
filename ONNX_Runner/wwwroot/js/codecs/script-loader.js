const pendingLoads = new Map();

// Loads a local UMD codec bundle once and resolves the requested global export.
export async function loadCodecLibrary(
    relativePath,
    resolveExport,
    exportName
) {
    const existing = resolveExport();

    if (existing) {
        return existing;
    }

    const url = new URL(
        relativePath,
        import.meta.url
    ).href;

    if (!pendingLoads.has(url)) {
        const loadPromise = new Promise(
            (resolve, reject) => {
                const script =
                    document.createElement(
                        'script'
                    );

                script.src = url;
                script.async = true;

                script.onload =
                    () => resolve();

                script.onerror =
                    () => {
                        reject(
                            new Error(
                                `Failed to load codec library: ${url}`
                            )
                        );
                    };

                document.head
                    .appendChild(
                        script
                    );
            }
        );

        pendingLoads.set(
            url,
            loadPromise
        );
    }

    try {
        await pendingLoads.get(url);
    } catch (error) {
        pendingLoads.delete(url);
        throw error;
    }

    const loaded = resolveExport();

    if (!loaded) {
        pendingLoads.delete(url);

        throw new Error(
            `${exportName} was not exposed by the loaded codec library.`
        );
    }

    return loaded;
}