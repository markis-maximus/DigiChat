import { defineConfig } from "vite";

// The normal build goes straight into the API's wwwroot so the backend serves
// the overlay at http://localhost:5170/overlay/ (the OBS Browser Source URL).
export default defineConfig(({ mode }) => {
  const isPublicBuild = mode === "public";
  return {
    base: "./",
    // Public releases deliberately omit public/. That folder holds the
    // streamer's generated manifest and their local sprite collection.
    publicDir: isPublicBuild ? false : "public",
    build: {
      // The public-mode publish build must never write into wwwroot. That
      // directory is the overlay actually being served, art and all, and
      // `emptyOutDir` would strip its manifest and sprites. The full repository
      // verifier first runs an ordinary build, which does empty and rebuild
      // wwwroot/overlay; never run it while DigiChat/OBS is serving or on
      // stream. Public staging keeps `dotnet publish` isolated; artifacts/ is
      // gitignored.
      outDir: isPublicBuild ? "../../artifacts/public-overlay" : "../DigiChat.Api/wwwroot/overlay",
      emptyOutDir: true,
    },
    server: {
      port: 5173,
    },
  };
});
