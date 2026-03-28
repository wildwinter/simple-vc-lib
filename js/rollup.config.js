export default {
  input: 'src/index.js',
  external: ['fs', 'path', 'child_process', 'os'],
  output: [
    {
      file: 'dist/simpleVcLib.js',
      format: 'es',
      sourcemap: true,
    },
    {
      file: 'dist/simpleVcLib.cjs',
      format: 'cjs',
      sourcemap: true,
    },
  ],
};
