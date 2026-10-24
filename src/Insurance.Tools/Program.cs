using Insurance.Tools;

// Command line for administrators. Connection strings come from the environment, never from arguments or code:
//   INSURANCE_ADMIN_CONNECTION   administrative connection to the application's database (migrate, users, import)
//   INSURANCE_MASTER_CONNECTION  administrative connection to the server's master database (create-database, logins)
return await Cli.RunAsync(args, Console.Out, Console.Error);
