import { defineConfig } from "vite";

export default defineConfig(({ mode }) => {
  const isPublicBuild = mode === "public";
  return {
    base: "./",
    // Public-mode releases stage outside wwwroot, so that publish phase never
    // overwrites the frontend being served locally. The full repository
    // verifier first runs an ordinary build, which does empty and rebuild
    // wwwroot/admin; never run it while DigiChat/OBS is serving or on stream.
    // Staging keeps the published artifact independent of whatever happens to
    // be sitting in the developer's wwwroot. Unlike the overlay, this project
    // has no public/ directory, so the publicDir switch below is only for
    // symmetry — there is no local art here to omit.
    publicDir: isPublicBuild ? false : "public",
    build: {
      outDir: isPublicBuild ? "../../artifacts/public-admin" : "../DigiChat.Api/wwwroot/admin",
      emptyOutDir: true,
    },
    server: {
      port: 5174,
    },
  };
});
